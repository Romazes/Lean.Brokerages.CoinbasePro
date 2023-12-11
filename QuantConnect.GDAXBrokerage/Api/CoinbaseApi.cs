﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2023 QuantConnect Corporation.
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
using RestSharp;
using System.Linq;
using Newtonsoft.Json;
using QuantConnect.Util;
using QuantConnect.Orders;
using QuantConnect.Brokerages;
using QuantConnect.Securities;
using System.Collections.Generic;
using QuantConnect.CoinbaseBrokerage.Models;
using QuantConnect.CoinbaseBrokerage.Models.Enums;
using QuantConnect.CoinbaseBrokerage.Models.Requests;
using BrokerageEnums = QuantConnect.CoinbaseBrokerage.Models.Enums;

namespace QuantConnect.CoinbaseBrokerage.Api;

public class CoinbaseApi : IDisposable
{
    /// <summary>
    /// Represents the maximum number of occurrences allowed per unit of time for a gate limit.
    /// </summary>
    /// <remarks>
    /// Refer to the documentation for more details: 
    /// <see href="https://docs.cloud.coinbase.com/advanced-trade-api/docs/rest-api-rate-limits"/>.
    /// </remarks>
    private const int maxGateLimitOccurrences = 30;

    /// <summary>
    /// Represents an instance of the Coinbase API client used for communication with the Coinbase API.
    /// </summary>
    private readonly CoinbaseApiClient _apiClient;

    /// <summary>
    /// Represents the prefix used for API endpoints in the application.
    /// </summary>
    private readonly string _apiPrefix = "/api/v3";

    /// <summary>
    /// Symbol mapper
    /// </summary>
    private ISymbolMapper SymbolMapper { get; }

    /// <summary>
    /// Security provider
    /// </summary>
    private ISecurityProvider SecurityProvider { get; }

    public CoinbaseApi(ISymbolMapper symbolMapper, ISecurityProvider securityProvider,
        string apiKey, string apiKeySecret, string restApiUrl)
    {
        SymbolMapper = symbolMapper;
        SecurityProvider = securityProvider;
        _apiClient = new CoinbaseApiClient(apiKey, apiKeySecret, restApiUrl, maxGateLimitOccurrences);
    }

    public IEnumerable<CoinbaseOrder> GetListOrders(BrokerageEnums.OrderStatus orderStatus)
    {
        var request = new RestRequest($"{_apiPrefix}/brokerage/orders/historical/batch", Method.GET);

        if (orderStatus != BrokerageEnums.OrderStatus.UNKNOWN_ORDER_STATUS)
        {
            request.AddQueryParameter("order_status", orderStatus.ToString());
        }

        var response = _apiClient.ExecuteRequest(request);

        return JsonConvert.DeserializeObject<CoinbaseOrderResponse>(response.Content).Orders;
    }

    public CoinbaseCancelOrder CancelOrders(List<string> brokerIds)
    {
        var request = new RestRequest($"{_apiPrefix}/brokerage/orders/batch_cancel", Method.POST);

        request.AddJsonBody(JsonConvert.SerializeObject(new { order_ids = brokerIds }));

        var response = _apiClient.ExecuteRequest(request);

        return JsonConvert.DeserializeObject<CoinbaseCancelOrders>(response.Content).Result.First();
    }

    /// <summary>
    /// Get snapshot information, by product ID, about the last trades (ticks), best bid/ask, and 24h volume.
    /// </summary>
    /// <param name="productId">The trading pair, i.e., 'BTC-USD'.</param>
    /// <param name="limit">Number of trades to return.</param>
    /// <returns>An instance of the <see cref="CoinbaseMarketTrades"/> struct.</returns>
    /// <exception cref="ArgumentException">Thrown when the provided productId is null or empty.</exception>
    public CoinbaseMarketTrades GetMarketTrades(string productId, int limit = 1)
    {
        if (string.IsNullOrEmpty(productId))
        {
            throw new ArgumentException("CoinbaseApi:GetMarketTrades(): productId is null or empty");
        }

        var request = new RestRequest($"{_apiPrefix}/brokerage/products/{productId}/ticker", Method.GET);

        request.AddQueryParameter("limit", limit.ToStringInvariant());

        var response = _apiClient.ExecuteRequest(request);

        return JsonConvert.DeserializeObject<CoinbaseMarketTrades>(response.Content);
    }

    public IRestResponse CreateOrder(Order leanOrder)
    {
        var placeOrderRequest = CreateRequest(leanOrder);

        var request = new RestRequest("/api/v3/brokerage/orders", Method.POST);

        request.AddJsonBody(JsonConvert.SerializeObject(placeOrderRequest,
            new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));

        var response = _apiClient.ExecuteRequest(request);

        return response;
    }

    private CoinbasePlaceOrderRequest CreateRequest(Order leanOrder)
    {
        if (leanOrder.Direction == OrderDirection.Hold) throw new NotSupportedException();

        var model = new CoinbasePlaceOrderRequest()
        {
            ClientOrderId = Guid.NewGuid(),
            ProductId = SymbolMapper.GetBrokerageSymbol(leanOrder.Symbol),
            Side = leanOrder.Direction == OrderDirection.Buy ? OrderSide.BUY : OrderSide.SELL,
        };

        switch (leanOrder)
        {
            case MarketOrder:
                model.OrderConfiguration = new OrderConfiguration { MarketIoc = new() };
                if (leanOrder.Direction == OrderDirection.Buy)
                {
                    var price = GetTickerPrice(leanOrder.Symbol, leanOrder.Direction);
                    model.OrderConfiguration.MarketIoc.QuoteSize = price * Math.Abs(leanOrder.Quantity);
                }
                else
                {
                    model.OrderConfiguration.MarketIoc.BaseSize = Math.Abs(leanOrder.Quantity);
                }
                break;
            case LimitOrder limitOrder when leanOrder.TimeInForce is Orders.TimeInForces.GoodTilCanceledTimeInForce:
                {
                    model.OrderConfiguration = new OrderConfiguration
                    {
                        LimitGtc = new()
                        {
                            BaseSize = Math.Abs(leanOrder.Quantity),
                            LimitPrice = limitOrder.LimitPrice,
                        }
                    };

                    if (leanOrder.Properties is GDAXOrderProperties prop)
                    {
                        model.OrderConfiguration.LimitGtc.PostOnly = prop.PostOnly;
                    }

                    break;
                }
            case LimitOrder limitOrder when leanOrder.TimeInForce is Orders.TimeInForces.GoodTilDateTimeInForce tilDate:
                {
                    model.OrderConfiguration = new OrderConfiguration
                    {
                        LimitGtd = new()
                        {
                            BaseSize = Math.Abs(leanOrder.Quantity),
                            LimitPrice = limitOrder.LimitPrice,
                            EndTime = tilDate.Expiry,
                        }
                    };

                    if (leanOrder.Properties is GDAXOrderProperties prop)
                    {
                        model.OrderConfiguration.LimitGtd.PostOnly = prop.PostOnly;
                    }

                    break;
                }
            case StopLimitOrder stopLimitOrder when leanOrder.TimeInForce is Orders.TimeInForces.GoodTilCanceledTimeInForce:
                var stopLimitGtc = new StopLimitGtc()
                {
                    BaseSize = Math.Abs(leanOrder.Quantity),
                    LimitPrice = stopLimitOrder.LimitPrice,
                    StopPrice = stopLimitOrder.StopPrice
                };

                var ticker = GetTickerPrice(leanOrder.Symbol, leanOrder.Direction);
                stopLimitGtc.StopDirection = stopLimitGtc.StopPrice > ticker ?
                    StopDirection.STOP_DIRECTION_STOP_UP :
                    StopDirection.STOP_DIRECTION_STOP_DOWN;

                model.OrderConfiguration = new() { StopLimitGtc = stopLimitGtc };
                break;
            case StopLimitOrder stopLimitOrder when leanOrder.TimeInForce is Orders.TimeInForces.GoodTilDateTimeInForce tilDate:
                var stopLimitGtd = new StopLimitGtd()
                {
                    EndTime = tilDate.Expiry,
                    StopPrice = stopLimitOrder.StopPrice,
                    LimitPrice = stopLimitOrder.LimitPrice,
                    BaseSize = Math.Abs(leanOrder.Quantity),
                };

                ticker = GetTickerPrice(leanOrder.Symbol, leanOrder.Direction);
                stopLimitGtd.StopDirection = stopLimitGtd.StopPrice > ticker ?
                    StopDirection.STOP_DIRECTION_STOP_UP :
                    StopDirection.STOP_DIRECTION_STOP_DOWN;

                model.OrderConfiguration = new() { StopLimitGtd = stopLimitGtd };
                break;
            default: throw new NotSupportedException($"Order type {leanOrder.Type.ToStringInvariant()} is not supported");
        };

        // TODO: Add CoinbaseOrderProperties
        if (false)
        {
            model.SelfTradePreventionId = Guid.NewGuid();
        }

        return model;
    }

    private decimal GetTickerPrice(Symbol symbol, OrderDirection leanOrderDirection)
    {
        var security = SecurityProvider.GetSecurity(symbol);
        var tickerPrice = leanOrderDirection == OrderDirection.Buy ? security.AskPrice : security.BidPrice;
        if (tickerPrice == 0)
        {
            var brokerageSymbol = SymbolMapper.GetBrokerageSymbol(symbol);
            var ticker = GetMarketTrades(brokerageSymbol);

            if (ticker.BestBid == 0 || ticker.BestAsk == 0)
            {
                throw new KeyNotFoundException(
                    $"CoinbaseBrokerage: Unable to resolve currency conversion pair: {symbol}");
            }

            tickerPrice = leanOrderDirection == OrderDirection.Buy ? ticker.BestAsk : ticker.BestBid;
        }

        return tickerPrice;
    }


    /// <summary>
    /// Performs application-defined tasks associated with freeing, releasing, or resetting resources.
    /// </summary>
    /// <remarks>
    /// This method disposes of the underlying API client safely to release any resources held by it.
    /// </remarks>
    public void Dispose()
    {
        _apiClient.DisposeSafely();
    }
}
