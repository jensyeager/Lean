﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using NodaTime;
using QuantConnect.Data;
using QuantConnect.Data.Fundamental;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Lean.Engine.DataFeeds
{
    /// <summary>
    /// Provides methods for apply the results of universe selection to an algorithm
    /// </summary>
    public class UniverseSelection
    {
        private readonly IDataFeed _dataFeed;
        private readonly IAlgorithm _algorithm;
        private readonly bool _isLiveMode;
        private readonly SecurityExchangeHoursProvider _hoursProvider = SecurityExchangeHoursProvider.FromDataFolder();

        /// <summary>
        /// Initializes a new instance of the <see cref="UniverseSelection"/> class
        /// </summary>
        /// <param name="dataFeed">The data feed to add/remove subscriptions from</param>
        /// <param name="algorithm">The algorithm to add securities to</param>
        /// <param name="isLiveMode">True for live mode, false for back test mode</param>
        public UniverseSelection(IDataFeed dataFeed, IAlgorithm algorithm, bool isLiveMode)
        {
            _dataFeed = dataFeed;
            _algorithm = algorithm;
            _isLiveMode = isLiveMode;
        }

        /// <summary>
        /// Applies universe selection the the data feed and algorithm
        /// </summary>
        /// <param name="date">The date used to get the current universe data</param>
        /// <param name="coarse">The coarse data used to perform a first pass at universe selection</param>
        public SecurityChanges ApplyUniverseSelection(DateTime date, IEnumerable<CoarseFundamental> coarse)
        {
            var selector = _algorithm.Universe;
            if (selector == null)
            {
                // a null value is indicative of not wanting to perform universe selection
                return SecurityChanges.None;
            }

            var limit = 1000; //daily/hourly limit
            var resolution = _algorithm.UniverseSettings.Resolution;
            switch (resolution)
            {
                case Resolution.Tick:
                    limit = _algorithm.Securities.TickLimit;
                    break;
                case Resolution.Second:
                    limit = _algorithm.Securities.SecondLimit;
                    break;
                case Resolution.Minute:
                    limit = _algorithm.Securities.MinuteLimit;
                    break;
            }

            // subtract current subscriptions that can't be removed
            limit -= _algorithm.Securities.Count(x => x.Value.Resolution == resolution && x.Value.HoldStock);

            if (limit < 1)
            {
                // if we don't have room for more securities then we can't really do anything here.
                _algorithm.Error("Unable to add  more securities from universe selection due to holding stock.");
                return SecurityChanges.None;
            }

            // perform initial filtering and limit the result
            var initialSelections = selector.SelectCoarse(coarse).Take(limit).ToList();

            var existingSubscriptions = _dataFeed.Subscriptions.ToHashSet(
                x => Tuple.Create(x.Configuration.Symbol, x.Configuration.Market)
                );

            // create a hash so we can detect removed subscriptions
            var selectedSubscriptions = initialSelections.ToHashSet(x => Tuple.Create(x.Symbol, x.Market));

            var additions = new List<Security>();
            var removals = new List<Security>();

            foreach (var subscription in _dataFeed.Subscriptions)
            {
                // never remove subscriptions set explicitly by the user
                if (subscription.IsUserDefined) continue;

                var config = subscription.Configuration;

                // never remove internal feeds
                if (config.IsInternalFeed) continue;

                // define the key for our hash
                var key = Tuple.Create(config.Symbol, config.Market);

                // if the subscription isn't currently selected, remove it from our data feed
                // but we can never remove subscriptions for which we hold stock currently
                if (!selectedSubscriptions.Contains(key))
                {
                    // let the algorithm know this security has been removed from the universe
                    removals.Add(subscription.Security);

                    // but don't physically remove it from the algorithm if we hold stock or have open orders against it
                    var openOrders = _algorithm.Transactions.GetOrders(x => x.Status.IsOpen() && x.Symbol == config.Symbol);
                    if (!subscription.Security.HoldStock && !openOrders.Any())
                    {
                        // we need to mark this security as untradeable while it has no data subscription
                        // it is expected that this function is called while in sync with the algo thread,
                        // so we can make direct edits to the security here
                        subscription.Security.Cache.Reset();

                        _dataFeed.RemoveSubscription(subscription.Security);
                    }
                }
            }

            var settings = _algorithm.UniverseSettings;

            // find new selections and add them to the algorithm
            foreach (var selection in initialSelections.Where(x => !existingSubscriptions.Contains(Tuple.Create(x.Symbol, x.Market))))
            {
                Security security;
                if (!_algorithm.Securities.TryGetValue(selection.Symbol, out security))
                {
                    // create the new security, the algorithm thread will add this at the appropriate time
                    security = SecurityManager.CreateSecurity(_algorithm.Portfolio, _algorithm.SubscriptionManager, _hoursProvider,
                        SecurityType.Equity, 
                        selection.Symbol,
                        settings.Resolution,
                        selection.Market,
                        settings.FillForward,
                        settings.Leverage,
                        settings.ExtendedMarketHours,
                        false
                        );
                }

                additions.Add(security);

                // add the new subscriptions to the data feed
                _dataFeed.AddSubscription(security, date, _algorithm.EndDate);
            }

            // return None if there's no changes, otherwise return what we've modified
            return additions.Count + removals.Count != 0 
                ? new SecurityChanges(additions, removals) 
                : SecurityChanges.None;
        }

        /// <summary>
        /// Gets the <see cref="CoarseFundamental"/> data for the specified market/date
        /// </summary>
        public static IEnumerable<CoarseFundamental> GetCoarseFundamentals(string market, DateTimeZone timeZone, DateTime date, bool isLiveMode)
        {
            var factory = new CoarseFundamental();
            var config = new SubscriptionDataConfig(typeof(CoarseFundamental), SecurityType.Equity, string.Empty, Resolution.Daily, market, timeZone, true, false, true);
            var reader = new BaseDataSubscriptionFactory(config, date, isLiveMode);
            var source = factory.GetSource(config, date, isLiveMode);
            return reader.Read(source).OfType<CoarseFundamental>();
        }
    }
}
