using System;
using Automatonymous;
using MassTransit;
using Microsoft.Extensions.Logging;
using Play.Identity.Contracts;
using Play.Inventory.Contracts;
using Play.Trading.Service.Activities;
using Play.Trading.Service.Contracts;
using Play.Trading.Service.SignalR;

namespace Play.Trading.Service.StateMachines
{
    public class PurchaseStateMachine : MassTransitStateMachine<PurchaseState>
    {
        private readonly MessageHub hub;
        private readonly ILogger<PurchaseStateMachine> logger;
        public State Accepted { get; }
        public State ItemsGranted { get; }
        public State Completed { get; }
        public State Faulted { get; }

        public Event<PurchaseRequested> PurchaseRequested { get; }
        public Event<GetPurchaseState> GetPurchaseState { get; }
        public Event<InventoryItemsGranted> InventoryItemsGranted { get; }
        public Event<GilDebited> GilDebited {get;}
        public Event<Fault<GrantItems>> GrantIntemsFaulted {get;}
        public Event<Fault<DebitGil>> DebitGilFaulted {get;}

        public PurchaseStateMachine(MessageHub hub, ILogger<PurchaseStateMachine> logger)
        {
            InstanceState(state => state.CurrentState);
            ConfigureEvents();
            ConfigureInitialState();
            ConfigureAny();
            ConfigureAccepted();
            ConfigureItemsGranted();
            ConfigureFaulted();
            ConfigureCompleted();
            this.hub = hub;
            this.logger = logger;
        }

        private void ConfigureEvents()
        {
            Event(() => PurchaseRequested);
            Event(() => GetPurchaseState);
            Event(() => InventoryItemsGranted);
            Event(() => GilDebited);
            Event(() => GrantIntemsFaulted, x => x.CorrelateById(
                context => context.Message.Message.CorrelationId
            ));
            Event(() => DebitGilFaulted, x => x.CorrelateById(
                context => context.Message.Message.CorrelationId
            ));
        }

        private void ConfigureInitialState()
        {
            Initially(
                When(PurchaseRequested)
                    .Then(context =>
                    {
                        context.Instance.UserId = context.Data.UserId;
                        context.Instance.ItemId = context.Data.ItemId;
                        context.Instance.Quantity = context.Data.Quantity;
                        context.Instance.Received = DateTimeOffset.UtcNow;
                        context.Instance.LastUpdated = context.Instance.Received;
                        logger.LogInformation(
                            "Calculating total price for purchase with CorrelationId {CorrelationId}...",
                            context.Instance.CorrelationId
                        );
                    })
                    .Activity(x => x.OfType<CalculatePurchaseTotalActivity>())
                    .Send(context => new GrantItems(
                        context.Instance.UserId,
                        context.Instance.ItemId,
                        context.Instance.Quantity,
                        context.Instance.CorrelationId))
                    .TransitionTo(Accepted)
                    .Catch<Exception>(ex => ex.
                        Then(context =>
                        {
                            context.Instance.ErrorMessage = context.Exception.Message;
                            context.Instance.LastUpdated = DateTimeOffset.UtcNow;
                            logger.LogError(
                                context.Exception,
                                "Could not calculate the total price of purchase with CorrelationId {CorrelationId}. Error: {ErrorMessage}",
                                context.Instance.CorrelationId,
                                context.Instance.ErrorMessage
                            );
                        })
                        .TransitionTo(Faulted)
                        .ThenAsync(async context => await hub.SendStatusAsync(context.Instance)))
            );
        }

        private void ConfigureAccepted()
        {
            During(Accepted,
                Ignore(PurchaseRequested),
                When(InventoryItemsGranted)
                    .Then(context => {
                        context.Instance.LastUpdated = DateTimeOffset.UtcNow;
                        logger.LogInformation(
                            "Items of purchase with CorrelationId {CorrelationId} have been granted to user {UserId}. ",
                            context.Instance.CorrelationId,
                            context.Instance.UserId
                        );
                    })
                    .Send(context => new DebitGil(
                        context.Instance.UserId,
                        context.Instance.PurchaseTotal.Value,
                        context.Instance.CorrelationId
                    ))
                    .TransitionTo(ItemsGranted),
                When(GrantIntemsFaulted)
                    .Then(context => {
                        context.Instance.ErrorMessage = context.Data.Exceptions[0].Message;
                        context.Instance.LastUpdated = DateTimeOffset.UtcNow;
                        logger.LogError(
                            "Could not grant items for purchase with CorrelationId {CorrelationId}. Error: {ErrorMessage}.",
                            context.Instance.CorrelationId,
                            context.Instance.ErrorMessage
                        );
                    })
                    .TransitionTo(Faulted)
                    .ThenAsync(async context => await hub.SendStatusAsync(context.Instance))
                );
        }

        public void ConfigureItemsGranted()
        {
            During(ItemsGranted,
                Ignore(PurchaseRequested),
                Ignore(InventoryItemsGranted),
                When(GilDebited)
                    .Then(context => {
                        context.Instance.LastUpdated = DateTimeOffset.UtcNow;
                        logger.LogInformation(
                            "Items total price of purchase with CorrelationId {CorrelationId} have been debited from user {UserId}. Purchase complete.",
                            context.Instance.CorrelationId,
                            context.Instance.UserId
                        );
                    })
                    .TransitionTo(Completed)
                    .ThenAsync(async context => await hub.SendStatusAsync(context.Instance)),
                When(DebitGilFaulted)
                    .Send(context => new SubtractItems(
                        context.Instance.UserId,
                        context.Instance.ItemId,
                        context.Instance.Quantity,
                        context.Instance.CorrelationId
                    ))
                    .Then(context =>
                    {
                        context.Instance.ErrorMessage = context.Data.Exceptions[0].Message;
                        context.Instance.LastUpdated = DateTimeOffset.UtcNow;
                        logger.LogError(
                            "Could not debit the total price of purchase with CorrelationId {CorrelationId} from user {UserId}. Error: {ErrorMessage}.",
                            context.Instance.CorrelationId,
                            context.Instance.UserId,
                            context.Instance.ErrorMessage
                        );
                    })
                    .TransitionTo(Faulted)
                    .ThenAsync(async context => await hub.SendStatusAsync(context.Instance))
             );
        }

        private void ConfigureCompleted()
        {
            During(Completed,
                Ignore(PurchaseRequested),
                Ignore(InventoryItemsGranted),
                Ignore(GilDebited)
            );
        }

        private void ConfigureAny()
        {
            DuringAny(
                When(GetPurchaseState)
                    .Respond(x => x.Instance)
            );
        }

        private void ConfigureFaulted()
        {
            During(Faulted,
                Ignore(PurchaseRequested),
                Ignore(InventoryItemsGranted),
                Ignore(GilDebited)
            );
        }
    }
}