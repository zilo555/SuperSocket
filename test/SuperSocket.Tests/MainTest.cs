using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperSocket.Connection;
using SuperSocket.Kestrel;
using SuperSocket.ProtoBase;
using SuperSocket.Server;
using SuperSocket.Server.Abstractions;
using SuperSocket.Server.Abstractions.Session;
using SuperSocket.Server.Host;
using Xunit;

namespace SuperSocket.Tests
{
    /// <summary>
    /// Run selected test case by command
    /// dotnet test --filter 'FullyQualifiedName=SuperSocket.Tests.SessionTest.TestCloseReason'
    /// </summary>
    [Trait("Category", "Basic")]
    public class MainTest : TestClassBase
    {
        public MainTest(ITestOutputHelper outputHelper)
            : base(outputHelper)
        {
            
        }

        [Fact]
        public async Task TestSessionCount() 
        {
            using (var server = CreateSocketServerBuilder<TextPackageInfo, LinePipelineFilter>()
                .UsePackageHandler(async (s, p) =>
                {
                    await s.SendAsync(Utf8Encoding.GetBytes("Hello World\r\n"));
                }).BuildAsServer())
            {
                Assert.Equal("TestServer", server.Name);

                Assert.True(await server.StartAsync(CancellationToken));
                OutputHelper.WriteLine("Started.");

                Assert.Equal(0, server.SessionCount);
                OutputHelper.WriteLine("SessionCount:" + server.SessionCount);

                var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await client.ConnectAsync(GetDefaultServerEndPoint(), CancellationToken);
                OutputHelper.WriteLine("Connected.");

                await Task.Delay(1000, CancellationToken);

                Assert.Equal(1, server.SessionCount);
                OutputHelper.WriteLine("SessionCount:" + server.SessionCount);

                client.Shutdown(SocketShutdown.Both);
                client.Close();

                await Task.Delay(1000, CancellationToken);

                Assert.Equal(0, server.SessionCount);
                OutputHelper.WriteLine("SessionCount:" + server.SessionCount);

                await server.StopAsync(CancellationToken);
            }            
        }
        
        [Fact]
        public void TestCustomConfigOptions() 
        {
            var hostConfigurator = new RegularHostConfigurator();
            var propName = "testPropName";
            var propValue = "testPropValue";

            using (var server = CreateSocketServerBuilder<TextPackageInfo, LinePipelineFilter>(hostConfigurator)
                .ConfigureAppConfiguration((HostBuilder, configBuilder) =>
                    {
                        configBuilder.AddInMemoryCollection(new Dictionary<string, string>
                        {
                            { $"serverOptions:values:{propName}", propValue }
                        });
                    }).BuildAsServer())
            {
                Assert.Equal(propValue, server.Options.Values[propName]);
            }
        }

        [Theory]
        [InlineData("Tls12", SslProtocols.Tls12, false)]
        //[InlineData("Tls13", SslProtocols.Tls13, false)] We cannot test it because TLS 1.3 client is only available in Windows
        [InlineData("Tls15", SslProtocols.None, true)]
        [InlineData("Tls13, Tls12", SslProtocols.Tls13 | SslProtocols.Tls12, false)]
        [InlineData("Tls13,Tls12", SslProtocols.Tls13 | SslProtocols.Tls12, false)]
        [InlineData("Tls13|Tls12", SslProtocols.Tls13 | SslProtocols.Tls12, true)]        
        public async Task TestSecurityOptions(string security, SslProtocols protocols, bool expectException) 
        {
            var hostConfigurator = new SecureHostConfigurator();
            var listener = default(ListenOptions);

            var autoResetEvent = new AutoResetEvent(false);

            var createServer = new Func<IServer>(() =>
            {
                return CreateSocketServerBuilder<TextPackageInfo, LinePipelineFilter>(hostConfigurator)
                    .ConfigureAppConfiguration((HostBuilder, configBuilder) =>
                    {
                        configBuilder.AddInMemoryCollection(new Dictionary<string, string>
                        {
                            { "serverOptions:listeners:0:authenticationOptions:enabledSslProtocols", security }
                        });
                    })
                    .ConfigureSuperSocket(serverOptions =>
                    {
                        listener = serverOptions.Listeners.FirstOrDefault();
                    })
                    .UseSessionHandler(onConnected: (session) =>
                        {
                            autoResetEvent.Set();
                            return ValueTask.CompletedTask;
                        },
                        onClosed: (session, reason) =>
                        {
                            autoResetEvent.Set();
                            return ValueTask.CompletedTask;
                        })
                    .BuildAsServer();
            });

            IServer server = null;

            if (!expectException)
                server = createServer();
            else
            {
                var exce = Assert.ThrowsAny<Exception>(() => 
                {
                    server = createServer();
                });

                return;
            }
            
            Assert.NotNull(listener);
            Assert.Equal(protocols, listener.AuthenticationOptions.EnabledSslProtocols);

            using (server)
            {
                Assert.Equal("TestServer", server.Name);

                Assert.True(await server.StartAsync(TestContext.Current.CancellationToken));
                OutputHelper.WriteLine("Started.");

                Assert.Equal(0, server.SessionCount);
                OutputHelper.WriteLine("SessionCount:" + server.SessionCount);

                using (var socket = CreateClient(hostConfigurator))
                {
                    var socketStream = await hostConfigurator.GetClientStream(socket);
                    autoResetEvent.WaitOne(1000);
                    Assert.Equal(1, server.SessionCount);
                    OutputHelper.WriteLine("SessionCount:" + server.SessionCount);              
                }

                autoResetEvent.WaitOne(1000);
                Assert.Equal(0, server.SessionCount);
                OutputHelper.WriteLine("SessionCount:" + server.SessionCount);

                await server.StopAsync(TestContext.Current.CancellationToken);
            }            
        }

        [Theory]
        [InlineData(typeof(RegularHostConfigurator))]
        [InlineData(typeof(KestralConnectionHostConfigurator))]
        [InlineData(typeof(UdpHostConfigurator))]
        public async Task TestSessionHandlers(Type hostConfiguratorType) 
        {
            var connected = false;
            var hostConfigurator = CreateObject<IHostConfigurator>(hostConfiguratorType);

            var autoResetEvent = new AutoResetEvent(false);

            using (var server = CreateSocketServerBuilder<TextPackageInfo, LinePipelineFilter>(hostConfigurator)
                .UseSessionHandler((s) =>
                {
                    connected = true;
                    autoResetEvent.Set();
                    return new ValueTask();
                }, (s, e) =>
                {
                    connected = false;
                    autoResetEvent.Set();
                    return new ValueTask();
                })
                .UsePackageHandler(async (s, p) =>
                {
                    if (p.Text == "CLOSE")
                        await s.CloseAsync(CloseReason.LocalClosing);            
                }).BuildAsServer())
            {
                Assert.Equal("TestServer", server.Name);

                Assert.True(await server.StartAsync(TestContext.Current.CancellationToken));
                OutputHelper.WriteLine("Started.");

                var client = hostConfigurator.CreateClient();
                var outputStream = default(Stream);

                if (hostConfigurator is UdpHostConfigurator)
                {                    
                    var buffer = Encoding.ASCII.GetBytes("HELLO\r\n");
                    outputStream = await hostConfigurator.GetClientStream(client);
                    outputStream.Write(buffer, 0, buffer.Length);
                    outputStream.Flush();
                }

                OutputHelper.WriteLine("Connected.");

                autoResetEvent.WaitOne(1000);

                Assert.True(connected);

                if (outputStream != null)
                {                    
                    var buffer = Encoding.ASCII.GetBytes("CLOSE\r\n");
                    outputStream.Write(buffer, 0, buffer.Length);
                    outputStream.Flush();
                }
                else
                {
                    client.Shutdown(SocketShutdown.Both);
                    client.Close();
                }                

                autoResetEvent.WaitOne(1000);

                if (outputStream != null)
                {
                    client.Close();
                }

                Assert.False(connected);

                await server.StopAsync(TestContext.Current.CancellationToken);
            }
        }

        [Fact]
        public async Task TestUseHostedService() 
        {
            var connected = false;

            using (var server = CreateSocketServerBuilder<TextPackageInfo, LinePipelineFilter>()
                .UseSessionHandler((s) =>
                {
                    connected = true;
                    return new ValueTask();
                }, (s, e) =>
                {
                    connected = false;
                    return new ValueTask();
                })
                .UseHostedService<SuperSocketServiceA>()
                .BuildAsServer())
            {
                Assert.Equal("TestServer", server.Name);

                Assert.True(await server.StartAsync(CancellationToken));
                OutputHelper.WriteLine("Started.");

                var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await client.ConnectAsync(GetDefaultServerEndPoint(), CancellationToken);
                OutputHelper.WriteLine("Connected.");

                await Task.Delay(1000, CancellationToken);

                Assert.True(connected);

                Assert.IsType<SuperSocketServiceA>(server);

                client.Shutdown(SocketShutdown.Both);
                client.Close();

                await Task.Delay(1000, CancellationToken);

                Assert.False(connected);

                await server.StopAsync(CancellationToken);
            }            
        }

        [Theory]
        [InlineData(typeof(RegularHostConfigurator))]
        [InlineData(typeof(SecureHostConfigurator))]
        [InlineData(typeof(UdpHostConfigurator))]
        [InlineData(typeof(KestralConnectionHostConfigurator))]
        public async Task TestHostStartupMinimalApi(Type hostConfiguratorType)
        {
            var hostConfigurator = CreateObject<IHostConfigurator>(hostConfiguratorType);

            var hostBuilder = WebApplication.CreateBuilder()
                .AsSuperSocketApplicationBuilder(serverHostBuilder =>
                    serverHostBuilder
                        .ConfigureAppConfiguration((hostingContext, config) =>
                        {
                            config.Sources.Clear();
                            config.AddJsonFile("Config/multiple_server.json", optional: false, reloadOnChange: true);
                        })
                        .AddServer<TextPackageInfo, LinePipelineFilter>(builder =>
                        {
                            hostConfigurator.Configure(builder);

                            builder
                                .ConfigureServerOptions((ctx, config) =>
                                {
                                    return config.GetSection("TestServer1");
                                })
                                .UsePackageHandler(async (IAppSession s, TextPackageInfo p) =>
                                {
                                    await s.SendAsync(Utf8Encoding.GetBytes("Hello World\r\n"));
                                });
                        })
                );

            using (var host = hostBuilder.Build())
            {
                await host.StartAsync(CancellationToken);
                await host.StopAsync(CancellationToken);
            }
        }

        [Theory]
        [InlineData(typeof(RegularHostConfigurator))]
        [InlineData(typeof(SecureHostConfigurator))]
        [InlineData(typeof(UdpHostConfigurator))]
        [InlineData(typeof(KestralConnectionHostConfigurator))]
        public async Task TestHostApplicationBuilderStartup(Type hostConfiguratorType)
        {
            var hostConfigurator = CreateObject<IHostConfigurator>(hostConfiguratorType);

            var hostBuilder = Host.CreateApplicationBuilder()
                .AsSuperSocketApplicationBuilder(serverHostBuilder =>
                    serverHostBuilder
                        .ConfigureAppConfiguration((hostingContext, config) =>
                        {
                            config.Sources.Clear();
                            config.AddJsonFile("Config/multiple_server.json", optional: false, reloadOnChange: true);
                        })
                        .AddServer<TextPackageInfo, LinePipelineFilter>(builder =>
                        {
                            hostConfigurator.Configure(builder);

                            builder
                                .ConfigureServerOptions((ctx, config) =>
                                {
                                    return config.GetSection("TestServer1");
                                })
                                .UsePackageHandler(async (IAppSession s, TextPackageInfo p) =>
                                {
                                    await s.SendAsync(Utf8Encoding.GetBytes("Hello World\r\n"));
                                });
                        })
                );

            using (var host = hostBuilder.Build())
            {
                await host.StartAsync(CancellationToken);
                await host.StopAsync(CancellationToken);
            }
        }

        [Fact]
        public async Task TestConfigureSocketOptions() 
        {
            var connected = false;
            var s = default(Socket);

            using (var server = CreateSocketServerBuilder<TextPackageInfo, LinePipelineFilter>()
                .UseSessionHandler(async (s) =>
                {
                    connected = true;
                    await Task.CompletedTask;
                }, async (s, e) =>
                {
                    connected = false;                    
                    await Task.CompletedTask;
                })
                .ConfigureSocketOptions(socket =>
                {
                    s = socket;
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 7);
                })
                .BuildAsServer())
            {
                Assert.Equal("TestServer", server.Name);

                Assert.True(await server.StartAsync(CancellationToken));
                OutputHelper.WriteLine("Started.");

                var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await client.ConnectAsync(GetDefaultServerEndPoint(), CancellationToken);
                OutputHelper.WriteLine("Connected.");

                await Task.Delay(1000, CancellationToken);

                Assert.True(connected);

                Assert.Equal(10, (int)s.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime));
                Assert.Equal(5, (int)s.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval));
                Assert.Equal(7, (int)s.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount));

                client.Shutdown(SocketShutdown.Both);
                client.Close();

                await Task.Delay(1000, CancellationToken);

                Assert.False(connected);

                await server.StopAsync(CancellationToken);
            }
        }

        [Theory]
        [InlineData(typeof(RegularHostConfigurator), typeof(TcpPipeConnection))]
        [InlineData(typeof(SecureHostConfigurator), typeof(StreamPipeConnection))]
        //[InlineData(typeof(UdpHostConfigurator), typeof(UdpConnectionStream))]
        [InlineData(typeof(KestralConnectionHostConfigurator), typeof(KestrelPipeConnection))]
        public async Task TestConnectionType(Type hostConfiguratorType, Type connectionType)
        {
            var hostConfigurator = CreateObject<IHostConfigurator>(hostConfiguratorType);
            var connection = default(IConnection);
            var resetEvent = new ManualResetEvent(false);
            
            using (var server = CreateSocketServerBuilder<TextPackageInfo, LinePipelineFilter>(hostConfigurator)
                .UseSessionHandler(onConnected: (session) =>
                {
                    connection = session.Connection;
                    resetEvent.Set();
                    return ValueTask.CompletedTask;
                }).BuildAsServer() as IServer)
            {
                Assert.True(await server.StartAsync(CancellationToken));
                Assert.Equal(0, server.SessionCount);

                using (var socket = CreateClient(hostConfigurator))
                using (var socketStream = await hostConfigurator.GetClientStream(socket))
                {
                    Assert.True(resetEvent.WaitOne(5000));
                }

                await server.StopAsync(CancellationToken);
            }

            Assert.IsType(connectionType, connection);
        }

        [Theory]
        [InlineData(typeof(RegularHostConfigurator))]
        [InlineData(typeof(SecureHostConfigurator))]
        [InlineData(typeof(KestralConnectionHostConfigurator))]
        public async Task TestConsoleProtocol(Type hostConfiguratorType)
        {
            var hostConfigurator = CreateObject<IHostConfigurator>(hostConfiguratorType);
            using (var server = CreateSocketServerBuilder<TextPackageInfo, LinePipelineFilter>(hostConfigurator)
                .UsePackageHandler(async (IAppSession s, TextPackageInfo p) =>
                {
                    await s.SendAsync(Utf8Encoding.GetBytes("Hello World\r\n"));
                }).BuildAsServer() as IServer)
            {            
                Assert.True(await server.StartAsync(CancellationToken));
                Assert.Equal(0, server.SessionCount);

                var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await client.ConnectAsync(hostConfigurator.GetServerEndPoint(), CancellationToken);                
                using (var stream = await hostConfigurator.GetClientStream(client))
                using (var streamReader = new StreamReader(stream, Utf8Encoding, true))
                using (var streamWriter = new StreamWriter(stream, Utf8Encoding, 1024 * 1024 * 4))
                {
                    await streamWriter.WriteAsync("Hello World\r\n");
                    await streamWriter.FlushAsync(CancellationToken);
                    var line = await streamReader.ReadLineAsync(CancellationToken);
                    Assert.Equal("Hello World", line);
                }

                await server.StopAsync(CancellationToken);
            }
        }

        [Theory]
        [InlineData(typeof(RegularHostConfigurator))]
        [InlineData(typeof(SecureHostConfigurator))]
        [InlineData(typeof(KestralConnectionHostConfigurator))]
        public async Task TestCloseAfterSend(Type hostConfiguratorType)
        {
            var hostConfigurator = CreateObject<IHostConfigurator>(hostConfiguratorType);
            using (var server = CreateSocketServerBuilder<TextPackageInfo, LinePipelineFilter>(hostConfigurator)
                .UsePackageHandler(async (IAppSession s, TextPackageInfo p) =>
                {
                    await s.SendAsync(Utf8Encoding.GetBytes("Hello World\r\n"));
                    await s.CloseAsync(CloseReason.LocalClosing);
                }).BuildAsServer() as IServer)
            {            
                Assert.True(await server.StartAsync(TestContext.Current.CancellationToken));
                Assert.Equal(0, server.SessionCount);

                var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await client.ConnectAsync(hostConfigurator.GetServerEndPoint(), TestContext.Current.CancellationToken);                
                using (var stream = await hostConfigurator.GetClientStream(client))
                using (var streamReader = new StreamReader(stream, Utf8Encoding, true))
                using (var streamWriter = new StreamWriter(stream, Utf8Encoding, 1024 * 1024 * 4))
                {
                    await streamWriter.WriteAsync("Hello World\r\n");
                    await streamWriter.FlushAsync(TestContext.Current.CancellationToken);
                    var line = await streamReader.ReadLineAsync(TestContext.Current.CancellationToken);
                    Assert.Equal("Hello World", line);
                }

                await server.StopAsync(TestContext.Current.CancellationToken);
            }
        }







        [Fact]
        [Trait("Category", "TestServiceProvider")]
        public async Task TestServiceProvider()
        {
            using (var server = CreateSocketServerBuilder<TextPackageInfo, LinePipelineFilter>()
                .ConfigureServices((ctx, services) =>
                {
                    services.AddSingleton<IHostConfigurator, RegularHostConfigurator>();
                }).BuildAsServer() as IServer)
            {            
                Assert.True(await server.StartAsync(CancellationToken)); 

                Assert.IsType<RegularHostConfigurator>(server.ServiceProvider.GetService<IHostConfigurator>());
                
                await server.StopAsync(CancellationToken);
            }
        }

        [Fact]
        public async Task TestStartWithDefaultConfig() 
        {
            var server = default(IServer);

            using (var host = SuperSocketHostBuilder.Create<TextPackageInfo, LinePipelineFilter>()
                .UseSessionHandler(s =>
                {
                    server = s.Server as IServer;
                    return new ValueTask();
                })
                .Build())
            {
                await host.StartAsync(CancellationToken);

                var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await client.ConnectAsync(GetDefaultServerEndPoint(), CancellationToken);
                OutputHelper.WriteLine("Connected.");

                await Task.Delay(1000, CancellationToken);

                Assert.Equal("TestServer", server.Name);

                Assert.Equal(1, server.SessionCount);
                OutputHelper.WriteLine("SessionCount:" + server.SessionCount);

                client.Shutdown(SocketShutdown.Both);
                client.Close();

                await Task.Delay(1000, CancellationToken);

                Assert.Equal(0, server.SessionCount);
                OutputHelper.WriteLine("SessionCount:" + server.SessionCount);

                await host.StopAsync(CancellationToken);
            }
        }






        class SuperSocketServiceA : SuperSocketService<TextPackageInfo>
        {
            public SuperSocketServiceA(IServiceProvider serviceProvider, IOptions<ServerOptions> serverOptions)
                : base(serviceProvider, serverOptions)
            {

            }
        }
    }
}
