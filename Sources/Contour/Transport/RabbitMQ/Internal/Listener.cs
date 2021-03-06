﻿using Newtonsoft.Json;

namespace Contour.Transport.RabbitMQ.Internal
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using Common.Logging;

    using Contour.Helpers;
    using Contour.Helpers.Scheduler;
    using Contour.Helpers.Timing;
    using Contour.Receiving;
    using Contour.Receiving.Consumers;
    using Contour.Validation;

    using global::RabbitMQ.Client.Events;

    /// <summary>
    /// Слушатель канала.
    /// </summary>
    internal class Listener : IDisposable
    {
        /// <summary>
        /// Поставщик каналов.
        /// </summary>
        private readonly IChannelProvider channelProvider;

        /// <summary>
        /// Обработчики сообщений.
        /// Каждому обработчику соответствует своя метка сообщения.
        /// </summary>
        private readonly IDictionary<MessageLabel, ConsumingAction> consumers = new ConcurrentDictionary<MessageLabel, ConsumingAction>();

        /// <summary>
        /// Порт канала, на который приходит сообщение.
        /// </summary>
        private readonly ISubscriptionEndpoint endpoint;

        /// <summary>
        /// Ожидания ответных сообщений на запрос.
        /// </summary>
        private readonly ConcurrentDictionary<string, Expectation> expectations = new ConcurrentDictionary<string, Expectation>();

        /// <summary>
        /// Хранилище заголовков входящего сообщения.
        /// </summary>
        private readonly IIncomingMessageHeaderStorage messageHeaderStorage;

        /// <summary>
        /// Объект синхронизации.
        /// </summary>
        private readonly object locker = new object();

        /// <summary>
        /// Журнал работы.
        /// </summary>
        private readonly ILog logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Реестр механизмов проверки сообщений.
        /// </summary>
        private readonly MessageValidatorRegistry validatorRegistry;

        /// <summary>
        /// Источник квитков отмены задач.
        /// </summary>
        private CancellationTokenSource cancellationTokenSource;

        /// <summary>
        /// Признак: слушатель потребляет сообщения.
        /// </summary>
        [SuppressMessage("StyleCop.CSharp.NamingRules", "SA1305:FieldNamesMustNotUseHungarianNotation", Justification = "Reviewed. Suppression is OK here.")]
        private bool isConsuming;

        /// <summary>
        /// Таймер, который отслеживает, что время ожидания ответа на запрос вышло.
        /// </summary>
        private ITicketTimer ticketTimer;

        /// <summary>
        /// Рабочие потоки выполняющее обработку сообщений.
        /// </summary>
        private IList<IWorker> workers;

        /// <summary>
        /// Инициализирует новый экземпляр класса <see cref="Listener"/>.
        /// </summary>
        /// <param name="channelProvider">
        /// Поставщик каналов.
        /// </param>
        /// <param name="endpoint">
        /// Прослушиваемый порт.
        /// </param>
        /// <param name="receiverOptions">
        /// Настройки получателя.
        /// </param>
        /// <param name="validatorRegistry">
        /// Реестр механизмов проверки сообщений.
        /// </param>
        public Listener(IChannelProvider channelProvider, ISubscriptionEndpoint endpoint, RabbitReceiverOptions receiverOptions, MessageValidatorRegistry validatorRegistry)
        {
            this.endpoint = endpoint;
            this.channelProvider = channelProvider;
            this.validatorRegistry = validatorRegistry;

            this.ReceiverOptions = receiverOptions;
            this.ReceiverOptions.GetIncomingMessageHeaderStorage();
            this.messageHeaderStorage = this.ReceiverOptions.GetIncomingMessageHeaderStorage().Value;

            // TODO: refactor
            this.Failed += _ =>
                {
                    if (this.HasFailed)
                    {
                        return;
                    }

                    this.HasFailed = true;
                    ((IBusAdvanced)channelProvider).Panic();
                }; // restarting the whole bus
        }

        /// <summary>
        /// Тип обработчика сообщений.
        /// </summary>
        /// <param name="delivery">
        /// Входящее сообщение.
        /// </param>
        internal delegate void ConsumingAction(RabbitDelivery delivery);

        /// <summary>
        /// Событие о сбое во время прослушивания порта.
        /// </summary>
        public event Action<Listener> Failed = l => { };

        /// <summary>
        /// Метки сообщений, которые может обработать слушатель.
        /// </summary>
        public IEnumerable<MessageLabel> AcceptedLabels
        {
            get
            {
                return this.consumers.Keys;
            }
        }

        /// <summary>
        /// Порт канала, который прослушивается на входящие сообщения.
        /// </summary>
        public ISubscriptionEndpoint Endpoint
        {
            get
            {
                return this.endpoint;
            }
        }

        /// <summary>
        /// Настройки получателя.
        /// </summary>
        public RabbitReceiverOptions ReceiverOptions { get; private set; }

        public bool HasFailed { get; private set; }

        /// <summary>
        /// Создает входящее сообщение.
        /// </summary>
        /// <param name="channel">
        /// Канал, по которому получено сообщение.
        /// </param>
        /// <param name="args">
        /// Аргументы, с которыми получено сообщение.
        /// </param>
        /// <returns>
        /// Входящее сообщение.
        /// </returns>
        public RabbitDelivery BuildDeliveryFrom(RabbitChannel channel, BasicDeliverEventArgs args)
        {
            return new RabbitDelivery(channel, args, this.ReceiverOptions.IsAcceptRequired());
        }

        /// <summary>
        /// Освобождает ресурсы.
        /// </summary>
        public void Dispose()
        {
            this.StopConsuming();
        }

        /// <summary>
        /// Возвращает задачу, с ожиданием ответа на запрос.
        /// </summary>
        /// <param name="correlationId">
        /// Корреляционный идентификатор, с помощью которого определяется принадлежность ответа определенному запросу.
        /// </param>
        /// <param name="expectedResponseType">
        /// Ожидаемый тип ответа.
        /// </param>
        /// <param name="timeout">
        /// Время ожидания ответа.
        /// </param>
        /// <returns>
        /// Задача ожидания ответа на запрос.
        /// </returns>
        public Task<IMessage> Expect(string correlationId, Type expectedResponseType, TimeSpan? timeout)
        {
            Expectation expectation;
            if (this.expectations.TryGetValue(correlationId, out expectation))
            {
                return expectation.Task;
            }

            // Используется блокировка, чтобы гарантировать что метод CreateExpectation будет вызван один раз, т.к. это не гарантируется реализацией метода GetOrAdd.
            lock (this.locker)
            {
                expectation = this.expectations.GetOrAdd(correlationId, c => this.CreateExpectation(c, expectedResponseType, timeout));
                return expectation.Task;
            }
        }

        /// <summary>
        /// Добавляет еще одного обработчика сообщений.
        /// </summary>
        /// <param name="label">
        /// Метка сообщения, которое может быть обработано.
        /// </param>
        /// <param name="consumer">
        /// Обработчик сообщения.
        /// </param>
        /// <param name="validator">
        /// Механизм проверки входящего сообщения.
        /// </param>
        /// <typeparam name="T">
        /// Тип входящего сообщения.
        /// </typeparam>
        public void RegisterConsumer<T>(MessageLabel label, IConsumerOf<T> consumer, IMessageValidator validator) where T : class
        {
            ConsumingAction consumingAction = delivery =>
                {
                    IConsumingContext<T> context = delivery.BuildConsumingContext<T>(label);

                    if (validator != null)
                    {
                        validator.Validate(context.Message).ThrowIfBroken();
                    }
                    else
                    {
                        this.validatorRegistry.Validate(context.Message);
                    }

                    this.logger.Trace(
                        m => m($"{typeof(T)}: получил сообщение " +
                               $"[{JsonConvert.SerializeObject(context.Message.Payload)}] на обработку"));

                    consumer.Handle(context);
                };

            this.consumers[label] = consumingAction;
        }

        /// <summary>
        /// Запускает обработку входящих сообщений.
        /// </summary>
        public void StartConsuming()
        {
            if (this.isConsuming)
            {
                return;
            }

            this.logger.InfoFormat("Starting consuming on [{0}].", this.endpoint.ListeningSource);

            this.cancellationTokenSource = new CancellationTokenSource();
            this.ticketTimer = new RoughTicketTimer(TimeSpan.FromSeconds(1));

            this.workers = Enumerable.Range(
                0, 
                (int)this.ReceiverOptions.GetParallelismLevel().Value)
                .Select(_ => ThreadWorker.StartNew(this.Consume, this.cancellationTokenSource.Token))
                .ToList();

            this.isConsuming = true;
        }

        /// <summary>
        /// Останавливает обработку входящих сообщений.
        /// </summary>
        public void StopConsuming()
        {
            // TODO: make more reliable
            lock (this.locker)
            {
                if (!this.isConsuming)
                {
                    return;
                }

                this.isConsuming = false;

                this.logger.InfoFormat("Stopping consuming on [{0}].", this.endpoint.ListeningSource);

                this.cancellationTokenSource.Cancel();

                WaitHandle.WaitAll(this.workers.Select(w => w.CompletionHandle).ToArray());
                this.workers.ForEach(w => w.Dispose());
                this.workers.Clear();

                this.ticketTimer.Dispose();
                this.expectations.Values.ForEach(e => e.Cancel());
            }
        }

        /// <summary>
        /// Проверяет поддерживает слушатель обработку сообщения с указанной меткой.
        /// </summary>
        /// <param name="label">
        /// Метка сообщения.
        /// </param>
        /// <returns>
        /// Если <c>true</c> - слушатель поддерживает обработку сообщений, иначе - <c>false</c>.
        /// </returns>
        public bool Supports(MessageLabel label)
        {
            return this.AcceptedLabels.Contains(label);
        }

        /// <summary>
        /// Доставляет сообщение до обработчика.
        /// </summary>
        /// <param name="delivery">
        /// Входящее сообщение.
        /// </param>
        protected void Deliver(RabbitDelivery delivery)
        {
            this.logger.Trace(m => m("Received delivery labeled [{0}] from [{1}] with consumer [{2}].", delivery.Label, delivery.Args.Exchange, delivery.Args.ConsumerTag));

            if (delivery.Headers.ContainsKey(Headers.OriginalMessageId))
            {
                this.logger.Trace(m => m("Сквозной идентификатор сообщения [{0}].", Headers.GetString(delivery.Headers, Headers.OriginalMessageId)));
            }

            if (delivery.Headers.ContainsKey(Headers.Breadcrumbs))
            {
                this.logger.Trace(m => m("Сообщение было обработано в конечных точках: [{0}].", Headers.GetString(delivery.Headers, Headers.Breadcrumbs)));
            }

            Stopwatch stopwatch = Stopwatch.StartNew();

            try
            {
                // TODO: refactor
                bool processed = this.TryHandleAsResponse(delivery);

                if (!processed)
                {
                    processed = this.TryHandleAsSubscription(delivery);
                }

                if (!processed)
                {
                    this.OnUnhandled(delivery);
                }
            }
            catch (Exception ex)
            {
                this.OnFailure(delivery, ex);
            }

            stopwatch.Stop();
            this.logger.Trace(m => m("Message labeled [{0}] processed in {1} ms.", delivery.Label, stopwatch.ElapsedMilliseconds));
        }

        /// <summary>
        /// Формирует ответ на запрос.
        /// </summary>
        /// <param name="delivery">
        /// Входящее сообщение, которое является ответом.
        /// </param>
        /// <param name="responseType">
        /// Тип ответного сообщения.
        /// </param>
        /// <returns>
        /// Сообщение с ответом на запрос.
        /// </returns>
        private IMessage BuildResponse(IDelivery delivery, Type responseType)
        {
            IMessage response = delivery.UnpackAs(responseType);

            this.validatorRegistry.Validate(response);

            return response;
        }

        /// <summary>
        /// Обрабатывает сообщение.
        /// </summary>
        /// <param name="cancellationToken">
        /// Сигнальный объект аварийного досрочного завершения обработки.
        /// </param>
        private void Consume(CancellationToken cancellationToken)
        {
            try
            {
                this.InternalConsume(cancellationToken);
            }
            catch (EndOfStreamException ex)
            {
                // The consumer was cancelled, the model closed, or the
                // connection went away.
                this.logger.DebugFormat("Reached EOS while listening on [{0}]. Stopping consuming.", ex, this.endpoint.ListeningSource);
                this.Failed(this);
            }
            catch (OperationCanceledException)
            {
                // do nothing, everything is fine
            }
            catch (Exception ex)
            {
                this.logger.ErrorFormat("Unexpected exception while listening on [{0}]. Stopping consuming.", ex, this.endpoint.ListeningSource);
                this.Failed(this);
            }
        }

        /// <summary>
        /// Создаёт ожидание ответа на запрос.
        /// </summary>
        /// <param name="correlationId">Корреляционный идентификатор, с помощью которого определяется принадлежность ответа определенному запросу.</param>
        /// <param name="expectedResponseType">Ожидаемый тип ответа.</param>
        /// <param name="timeout">Время ожидания ответа.</param>
        /// <returns>Ожидание ответа на запрос.</returns>
        private Expectation CreateExpectation(string correlationId, Type expectedResponseType, TimeSpan? timeout)
        {
            long? timeoutTicket = null;
            if (timeout.HasValue)
            {
                timeoutTicket = this.ticketTimer.Acquire(
                    timeout.Value,
                    () =>
                    {
                        this.OnResponseTimeout(correlationId);
                    });
            }

            return new Expectation(d => this.BuildResponse(d, expectedResponseType), timeoutTicket);
        }

        private void InternalConsume(CancellationToken cancellationToken)
        {
            var channel = (RabbitChannel)this.channelProvider.OpenChannel();
            channel.Failed += (ch, args) => this.Failed(this);

            if (this.ReceiverOptions.GetQoS().HasValue)
            {
                channel.SetQos(
                    this.ReceiverOptions.GetQoS().Value);
            }

            CancellableQueueingConsumer consumer = channel.BuildCancellableConsumer(cancellationToken);
            channel.StartConsuming(this.endpoint.ListeningSource, this.ReceiverOptions.IsAcceptRequired(), consumer);

            consumer.ConsumerCancelled += (sender, args) =>
            {
                this.logger.InfoFormat("Consumer [{0}] was cancelled.", args.ConsumerTag);
                this.Failed(this);
            };

            while (!cancellationToken.IsCancellationRequested)
            {
                BasicDeliverEventArgs message = consumer.Dequeue();
                this.Deliver(this.BuildDeliveryFrom(channel, message));
            }

            // TODO: resolve deadlock
            // channel.TryStopConsuming(consumerTag);
        }

        /// <summary>
        /// Обработчик события о сбое обработки сообщения.
        /// </summary>
        /// <param name="delivery">
        /// Сообщение, обработка которого привела к сбою.
        /// </param>
        /// <param name="exception">
        /// Исключение сгенерированное во время сбоя.
        /// </param>
        private void OnFailure(RabbitDelivery delivery, Exception exception)
        {
            this.logger.Warn(m => m("Failed to process message labeled [{0}] on queue [{1}].", delivery.Label, this.endpoint.ListeningSource), exception);

            this.ReceiverOptions.GetFailedDeliveryStrategy()
                .Value.Handle(new RabbitFailedConsumingContext(delivery, exception));
        }

        /// <summary>
        /// Обрабатывает превышение времени ожидания ответа.
        /// </summary>
        /// <param name="correlationId">Корреляционный идентификатор, с помощью которого определяется принадлежность ответа определенному запросу.</param>
        private void OnResponseTimeout(string correlationId)
        {
            Expectation expectation;
            if (this.expectations.TryRemove(correlationId, out expectation))
            {
                expectation.Timeout();
            }
        }

        /// <summary>
        /// Обработчик события о ненайденном обработчике сообщений.
        /// </summary>
        /// <param name="delivery">
        /// Сообщение, для которого не найден обработчик.
        /// </param>
        private void OnUnhandled(RabbitDelivery delivery)
        {
            this.logger.Warn(m => m("No handler for message labeled [{0}] on queue [{1}].", delivery.Label, this.endpoint.ListeningSource));

            this.ReceiverOptions.GetUnhandledDeliveryStrategy()
                .Value.Handle(new RabbitUnhandledConsumingContext(delivery));
        }

        /// <summary>
        /// Пытается обработать сообщение как запрос.
        /// </summary>
        /// <param name="delivery">
        /// Входящее сообщение.
        /// </param>
        /// <returns>
        /// Если <c>true</c> - тогда сообщение обработано как запрос, иначе - <c>false</c>.
        /// </returns>
        private bool TryHandleAsResponse(RabbitDelivery delivery)
        {
            if (!delivery.IsResponse)
            {
                return false;
            }

            string correlationId = delivery.CorrelationId;

            Expectation expectation;
            if (!this.expectations.TryRemove(correlationId, out expectation))
            {
                return false;
            }

            if (expectation.TimeoutTicket.HasValue)
            {
                this.ticketTimer.Cancel(expectation.TimeoutTicket.Value);
            }

            expectation.Complete(delivery);
            return true;
        }

        /// <summary>
        /// Пытается обработать сообщение как одностороннее.
        /// </summary>
        /// <param name="delivery">
        /// Входящее сообщение.
        /// </param>
        /// <returns>
        /// Если <c>true</c> - входящее сообщение обработано, иначе <c>false</c>.
        /// </returns>
        private bool TryHandleAsSubscription(RabbitDelivery delivery)
        {
            ConsumingAction consumingAction;
            if (!this.consumers.TryGetValue(delivery.Label, out consumingAction))
            {
                // NOTE: this is needed for compatibility with v1 of ServiceBus
                if (this.consumers.Count == 1)
                {
                    consumingAction = this.consumers.Values.Single();
                }

                if (consumingAction == null)
                {
                    this.consumers.TryGetValue(MessageLabel.Any, out consumingAction);
                }
            }

            if (consumingAction != null)
            {
                this.messageHeaderStorage.Store(delivery.Headers);
                consumingAction(delivery);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Ожидание ответа на запрос.
        /// </summary>
        internal class Expectation
        {
            /// <summary>
            /// Источник сигнальных объектов об аварийном завершении задачи.
            /// </summary>
            private readonly TaskCompletionSource<IMessage> completionSource;

            /// <summary>
            /// Построитель ответа.
            /// </summary>
            private readonly Func<IDelivery, IMessage> responseBuilderFunc;

            /// <summary>
            /// Секундомер для замера длительности ожидания ответа.
            /// </summary>
            private readonly Stopwatch completionStopwatch;

            /// <summary>
            /// Инициализирует новый экземпляр класса <see cref="Expectation"/>.
            /// </summary>
            /// <param name="responseBuilderFunc">
            /// Построитель ответа.
            /// </param>
            /// <param name="timeoutTicket">
            /// Квиток об учете времени ожидания ответа.
            /// </param>
            public Expectation(Func<IDelivery, IMessage> responseBuilderFunc, long? timeoutTicket)
            {
                this.responseBuilderFunc = responseBuilderFunc;
                this.TimeoutTicket = timeoutTicket;

                this.completionSource = new TaskCompletionSource<IMessage>();
                this.completionStopwatch = Stopwatch.StartNew();
            }

            /// <summary>
            /// Задача завершения ожидания.
            /// </summary>
            public Task<IMessage> Task
            {
                get
                {
                    return this.completionSource.Task;
                }
            }

            /// <summary>
            /// Квиток об учете времени ожидания ответа.
            /// </summary>
            public long? TimeoutTicket { get; private set; }

            /// <summary>
            /// Отменяет ожидание ответа.
            /// </summary>
            public void Cancel()
            {
                this.completionSource.TrySetException(new OperationCanceledException());
            }

            /// <summary>
            /// Выполняет обработку ответа на запрос.
            /// </summary>
            /// <param name="delivery">
            /// Входящее сообщение - ответ на запрос.
            /// </param>
            public void Complete(RabbitDelivery delivery)
            {
                try
                {
                    this.completionStopwatch.Stop();
                    IMessage response = this.responseBuilderFunc(delivery);
                    this.completionSource.TrySetResult(response);
                }
                catch (Exception ex)
                {
                    this.completionSource.TrySetException(ex);
                    throw;
                }
            }

            /// <summary>
            /// Устанавливает, что при ожидании вышло время, за которое должен был быть получен ответ.ё
            /// </summary>
            public void Timeout()
            {
                this.completionSource.TrySetException(new TimeoutException());
            }
        }
    }
}
