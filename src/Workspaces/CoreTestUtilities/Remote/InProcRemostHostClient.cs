﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.Remoting;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Experiments;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Serialization;
using Microsoft.CodeAnalysis.TodoComments;
using Microsoft.ServiceHub.Framework;
using Nerdbank.Streams;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using StreamJsonRpc;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.Remote.Testing
{
    internal sealed partial class InProcRemoteHostClient : RemoteHostClient, IRemoteHostServiceCallback
    {
        private readonly HostWorkspaceServices _workspaceServices;
        private readonly InProcRemoteServices _inprocServices;
        private readonly RemoteEndPoint _endPoint;
        private readonly TraceSource _logger;

        public static async Task<RemoteHostClient> CreateAsync(HostWorkspaceServices services, RemoteHostTestData testData)
        {
            var inprocServices = new InProcRemoteServices(services, testData);

            var remoteHostStream = await inprocServices.RequestServiceAsync(WellKnownServiceHubService.RemoteHost).ConfigureAwait(false);

            var instance = new InProcRemoteHostClient(services, inprocServices, remoteHostStream);

            // make sure connection is done right
            var uiCultureLCIDE = 0;
            var cultureLCID = 0;

            await instance._endPoint.InvokeAsync(
                nameof(IRemoteHostService.InitializeGlobalState),
                new object?[] { uiCultureLCIDE, cultureLCID },
                CancellationToken.None).ConfigureAwait(false);

            instance.Started();

            // return instance
            return instance;
        }

        private InProcRemoteHostClient(
            HostWorkspaceServices services,
            InProcRemoteServices inprocServices,
            Stream stream)
        {
            _workspaceServices = services;
            _logger = new TraceSource("Default");

            _inprocServices = inprocServices;

            _endPoint = new RemoteEndPoint(stream, _logger, incomingCallTarget: this);
            _endPoint.Disconnected += OnDisconnected;
            _endPoint.StartListening();
        }

        public static async Task<InProcRemoteHostClient> GetTestClientAsync(Workspace workspace)
        {
            var client = (InProcRemoteHostClient?)await TryGetClientAsync(workspace, CancellationToken.None).ConfigureAwait(false);
            Contract.ThrowIfNull(client);
            return client;
        }

        public RemoteWorkspace GetRemoteWorkspace()
            => TestData.WorkspaceManager.GetWorkspace();

        /// <summary>
        /// Remote API.
        /// </summary>
        public Task GetAssetsAsync(int scopeId, Checksum[] checksums, string pipeName, CancellationToken cancellationToken)
            => RemoteEndPoint.WriteDataToNamedPipeAsync(
                pipeName,
                (scopeId, checksums),
                (writer, data, cancellationToken) => RemoteHostAssetSerialization.WriteDataAsync(
                    writer, _workspaceServices.GetRequiredService<ISolutionAssetStorageProvider>().AssetStorage, _workspaceServices.GetRequiredService<ISerializerService>(), data.scopeId, data.checksums, cancellationToken),
                cancellationToken);

        /// <summary>
        /// Remote API.
        /// </summary>
        public Task<bool> IsExperimentEnabledAsync(string experimentName, CancellationToken cancellationToken)
            => _workspaceServices.GetRequiredService<IExperimentationService>().IsExperimentEnabled(experimentName) ? SpecializedTasks.True : SpecializedTasks.False;

        public RemoteHostTestData TestData => _inprocServices.TestData;

        public void RegisterService(RemoteServiceName serviceName, Func<Stream, IServiceProvider, ServiceActivationOptions, ServiceBase> serviceCreator)
            => _inprocServices.RegisterService(serviceName, serviceCreator);

        public override async ValueTask<RemoteServiceConnection<T>> CreateConnectionAsync<T>(object? callbackTarget, CancellationToken cancellationToken)
        {
            var options = new ServiceActivationOptions();

            if (callbackTarget is not null)
            {
                options.ClientRpcTarget = callbackTarget;
            }

            var assetStorage = _workspaceServices.GetRequiredService<ISolutionAssetStorageProvider>().AssetStorage;
            var descriptor = ServiceDescriptors.GetServiceDescriptor(typeof(T), isRemoteHost64Bit: IntPtr.Size == 8);

#pragma warning disable ISB001 // Dispose of proxies - caller disposes
            var proxy = await _inprocServices.ServiceBroker.GetProxyAsync<T>(descriptor, options, cancellationToken).ConfigureAwait(false);
#pragma warning restore

            Contract.ThrowIfNull(proxy);
            return new BrokeredServiceConnection<T>(proxy, assetStorage, errorReportingService: null);
        }

        public override async Task<RemoteServiceConnection> CreateConnectionAsync(RemoteServiceName serviceName, object? callbackTarget, CancellationToken cancellationToken)
        {
            // get stream from service hub to communicate service specific information 
            // this is what consumer actually use to communicate information
            var serviceStream = await _inprocServices.RequestServiceAsync(serviceName).ConfigureAwait(false);

            return new JsonRpcConnection(_workspaceServices, _logger, callbackTarget, serviceStream, poolReclamation: null);
        }

        public override void Dispose()
        {
            // we are asked to disconnect. unsubscribe and dispose to disconnect
            _endPoint.Disconnected -= OnDisconnected;
            _endPoint.Dispose();

            base.Dispose();
        }

        private void OnDisconnected(JsonRpcDisconnectedEventArgs e)
            => Dispose();

        public sealed class ServiceProvider : IServiceProvider
        {
            public readonly TraceSource TraceSource;
            public readonly RemoteHostTestData TestData;

            public ServiceProvider(TraceSource traceSource, RemoteHostTestData testData)
            {
                TraceSource = traceSource;
                TestData = testData;
            }

            public object GetService(Type serviceType)
            {
                if (typeof(TraceSource) == serviceType)
                {
                    return TraceSource;
                }

                if (typeof(RemoteHostTestData) == serviceType)
                {
                    return TestData;
                }

                throw ExceptionUtilities.UnexpectedValue(serviceType);
            }
        }

        private sealed class InProcServiceBroker : IServiceBroker
        {
            private readonly InProcRemoteServices _services;

            public InProcServiceBroker(InProcRemoteServices services)
            {
                _services = services;
            }

            public event EventHandler<BrokeredServicesChangedEventArgs>? AvailabilityChanged { add { } remove { } }

            // This method is currently not needed for our IServiceBroker usage patterns.
            public ValueTask<IDuplexPipe?> GetPipeAsync(ServiceMoniker serviceMoniker, ServiceActivationOptions options, CancellationToken cancellationToken)
                => throw ExceptionUtilities.Unreachable;

            public ValueTask<T?> GetProxyAsync<T>(ServiceRpcDescriptor descriptor, ServiceActivationOptions options, CancellationToken cancellationToken) where T : class
            {
                var pipePair = FullDuplexStream.CreatePipePair();

                var clientConnection = descriptor.ConstructRpcConnection(pipePair.Item2);

                Contract.ThrowIfFalse(options.ClientRpcTarget is null == descriptor.ClientInterface is null);

                if (descriptor.ClientInterface != null)
                {
                    Contract.ThrowIfNull(options.ClientRpcTarget);
                    clientConnection.AddLocalRpcTarget(options.ClientRpcTarget);
                }

                // Clear RPC target so that the server connection is forced to create a new proxy for the callback
                // instead of just invoking the callback object directly (this emulates the product that does
                // not serialize the callback object over).
                options.ClientRpcTarget = null;

                // Creates service instance and connects it to the pipe. 
                // We don't need to store the instance anywhere.
                _ = _services.CreateBrokeredService(descriptor, pipePair.Item1, options);

                clientConnection.StartListening();

                return new ValueTask<T?>(clientConnection.ConstructRpcClient<T>());
            }
        }

        private sealed class InProcRemoteServices
        {
            private readonly ServiceProvider _serviceProvider;
            private readonly Dictionary<ServiceMoniker, Func<object>> _inProcBrokeredServicesMap = new();
            private readonly Dictionary<ServiceMoniker, BrokeredServiceBase.IFactory> _remoteBrokeredServicesMap = new();
            private readonly Dictionary<RemoteServiceName, Func<Stream, IServiceProvider, ServiceActivationOptions, ServiceBase>> _factoryMap = new();
            private readonly Dictionary<string, WellKnownServiceHubService> _serviceNameMap = new();

            public readonly IServiceBroker ServiceBroker;

            public InProcRemoteServices(HostWorkspaceServices workspaceServices, RemoteHostTestData testData)
            {
                var remoteLogger = new TraceSource("inprocRemoteClient");

                _serviceProvider = new ServiceProvider(remoteLogger, testData);

                ServiceBroker = new InProcServiceBroker(this);

                RegisterService(WellKnownServiceHubService.RemoteHost, (s, p, o) => new RemoteHostService(s, p));
                RegisterService(WellKnownServiceHubService.CodeAnalysis, (s, p, o) => new CodeAnalysisService(s, p));
                RegisterService(WellKnownServiceHubService.RemoteSymbolSearchUpdateEngine, (s, p, o) => new RemoteSymbolSearchUpdateEngine(s, p));
                RegisterInProcBrokeredService(SolutionAssetProvider.ServiceDescriptor, () => new SolutionAssetProvider(workspaceServices));
                RegisterRemoteBrokeredService(new RemoteDesignerAttributeService.Factory());
                RegisterRemoteBrokeredService(new RemoteProjectTelemetryService.Factory());
                RegisterRemoteBrokeredService(new RemoteTodoCommentsService.Factory());
                RegisterRemoteBrokeredService(new RemoteDiagnosticAnalyzerService.Factory());
                RegisterRemoteBrokeredService(new RemoteSemanticClassificationService.Factory());
                RegisterRemoteBrokeredService(new RemoteSemanticClassificationCacheService.Factory());
                RegisterService(WellKnownServiceHubService.LanguageServer, (s, p, o) => new LanguageServer(s, p));
            }

            public RemoteHostTestData TestData => _serviceProvider.TestData;

            public void RegisterService(RemoteServiceName name, Func<Stream, IServiceProvider, ServiceActivationOptions, ServiceBase> serviceFactory)
            {
                _factoryMap.Add(name, serviceFactory);
                _serviceNameMap.Add(name.ToString(isRemoteHost64Bit: IntPtr.Size == 8), name.WellKnownService);
            }

            public Task<Stream> RequestServiceAsync(RemoteServiceName serviceName)
            {
                var factory = _factoryMap[serviceName];
                var streams = FullDuplexStream.CreatePair();
                return Task.FromResult<Stream>(new WrappedStream(factory(streams.Item1, _serviceProvider, default), streams.Item2));
            }

            public void RegisterInProcBrokeredService(ServiceDescriptor serviceDescriptor, Func<object> serviceFactory)
            {
                _inProcBrokeredServicesMap.Add(serviceDescriptor.Moniker, serviceFactory);
            }

            public void RegisterRemoteBrokeredService(BrokeredServiceBase.IFactory serviceFactory)
            {
                var moniker = ServiceDescriptors.GetServiceDescriptor(serviceFactory.ServiceType, isRemoteHost64Bit: IntPtr.Size == 8).Moniker;
                _remoteBrokeredServicesMap.Add(moniker, serviceFactory);
            }

            public object CreateBrokeredService(ServiceRpcDescriptor descriptor, IDuplexPipe pipe, ServiceActivationOptions options)
            {
                if (_inProcBrokeredServicesMap.TryGetValue(descriptor.Moniker, out var inProcFactory))
                {
                    // This code is similar to service creation implemented in BrokeredServiceBase.FactoryBase.
                    // Currently don't support callback creation as we don't have in-proc service with callbacks yet.
                    Contract.ThrowIfFalse(descriptor.ClientInterface == null);

                    var serviceConnection = descriptor.ConstructRpcConnection(pipe);
                    var service = inProcFactory();

                    serviceConnection.AddLocalRpcTarget(service);
                    serviceConnection.StartListening();

                    return service;
                }

                if (_remoteBrokeredServicesMap.TryGetValue(descriptor.Moniker, out var remoteFactory))
                {
                    return remoteFactory.Create(pipe, _serviceProvider, options, ServiceBroker);
                }

                throw ExceptionUtilities.UnexpectedValue(descriptor.Moniker);
            }

            private sealed class WrappedStream : Stream
            {
                private readonly IDisposable _service;
                private readonly Stream _stream;

                public WrappedStream(IDisposable service, Stream stream)
                {
                    // tie service's lifetime with that of stream
                    _service = service;
                    _stream = stream;
                }

                public override long Position
                {
                    get { return _stream.Position; }
                    set { _stream.Position = value; }
                }

                public override int ReadTimeout
                {
                    get { return _stream.ReadTimeout; }
                    set { _stream.ReadTimeout = value; }
                }

                public override int WriteTimeout
                {
                    get { return _stream.WriteTimeout; }
                    set { _stream.WriteTimeout = value; }
                }

                public override bool CanRead => _stream.CanRead;
                public override bool CanSeek => _stream.CanSeek;
                public override bool CanWrite => _stream.CanWrite;
                public override long Length => _stream.Length;
                public override bool CanTimeout => _stream.CanTimeout;

                public override void Flush() => _stream.Flush();
                public override Task FlushAsync(CancellationToken cancellationToken) => _stream.FlushAsync(cancellationToken);

                public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);
                public override void SetLength(long value) => _stream.SetLength(value);

                public override int ReadByte() => _stream.ReadByte();
                public override void WriteByte(byte value) => _stream.WriteByte(value);

                public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
                public override void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);

                public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _stream.ReadAsync(buffer, offset, count, cancellationToken);
                public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _stream.WriteAsync(buffer, offset, count, cancellationToken);

#if NET5_0 // nullability annotations differ
                public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) => _stream.BeginRead(buffer, offset, count, callback, state);
                public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) => _stream.BeginWrite(buffer, offset, count, callback, state);
#else
                public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object? state) => _stream.BeginRead(buffer, offset, count, callback, state);
                public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object? state) => _stream.BeginWrite(buffer, offset, count, callback, state);
#endif
                public override int EndRead(IAsyncResult asyncResult) => _stream.EndRead(asyncResult);
                public override void EndWrite(IAsyncResult asyncResult) => _stream.EndWrite(asyncResult);

                public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) => _stream.CopyToAsync(destination, bufferSize, cancellationToken);

                public override object InitializeLifetimeService()
                    => throw new NotSupportedException();

#if !NETCOREAPP
                public override ObjRef CreateObjRef(Type requestedType)
                    => throw new NotSupportedException();
#endif

                public override void Close()
                {
                    _service.Dispose();
                    _stream.Close();
                }

                protected override void Dispose(bool disposing)
                {
                    base.Dispose(disposing);

                    _service.Dispose();
                    _stream.Dispose();
                }
            }
        }
    }
}
