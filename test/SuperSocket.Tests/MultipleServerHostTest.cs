using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperSocket.ProtoBase;
using SuperSocket.Server;
using SuperSocket.Server.Abstractions;
using SuperSocket.Server.Abstractions.Session;
using SuperSocket.Server.Host;
using Xunit;

namespace SuperSocket.Tests
{
    [Trait("Category", "MultipleServerHost")]
    public class MultipleServerHostTest : TestClassBase
    {
        public MultipleServerHostTest(ITestOutputHelper outputHelper)
            : base(outputHelper)
        {
            
        }

        [Theory]
        [InlineData(typeof(RegularHostConfigurator))]
        [InlineData(typeof(SecureHostConfigurator))]
        [InlineData(typeof(UdpHostConfigurator))]
        [InlineData(typeof(KestralConnectionHostConfigurator))]
        public async Task TestMultipleHostStartup(Type hostConfiguratorType)
        {
            var hostConfigurator = CreateObject<IHostConfigurator>(hostConfiguratorType);

            var hostBuilder = MultipleServerHostBuilder.Create()
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
                });

            using(var host = hostBuilder.Build())
            {
                await host.StartAsync();
                await host.StopAsync();
            }
        }

        [Fact]
        public async Task TestMultipleServerHost()
        {
            var serverName1 = "TestServer1";
            var serverName2 = "TestServer2";

            var server1 = default(IServer);
            var server2 = default(IServer);

            IHostEnvironment actualHostEvn = null;

            var hostBuilder = MultipleServerHostBuilder.Create()
                .ConfigureAppConfiguration((hostingContext, config) =>
                {
                    actualHostEvn = hostingContext.HostingEnvironment;
                    config.Sources.Clear();
                    config.AddJsonFile("Config/multiple_server.json", optional: false, reloadOnChange: true);
                })
                .ConfigureServices((hostingContext, services) =>
                {
                    services.AddSingleton<MyTestService>();
                })
                .AddServer<SuperSocketServiceA, TextPackageInfo, LinePipelineFilter>(builder =>
                {
                    builder
                    .ConfigureServerOptions((ctx, config) =>
                    {
                        return config.GetSection(serverName1);
                    }).UseSessionHandler(async (s) =>
                    {
                        server1 = s.Server as IServer;
                        await s.SendAsync(Utf8Encoding.GetBytes($"{s.Server.Name}\r\n"));
                    })
                    .UseInProcSessionContainer()
                    .ConfigureServices((ctx, services) => services.AddSingleton<MyLocalTestService>());
                })
                .AddServer<SuperSocketServiceB, TextPackageInfo, LinePipelineFilter>(builder =>
                {
                    builder
                    .ConfigureServerOptions((ctx, config) =>
                    {
                        return config.GetSection(serverName2);
                    }).UseSessionHandler(async (s) =>
                    {
                        server2 = s.Server as IServer;
                        await s.SendAsync(Utf8Encoding.GetBytes($"{s.Server.Name}\r\n"));
                    })
                    .UseInProcSessionContainer()
                    .ConfigureServices((ctx, services) => services.AddSingleton<MyLocalTestService>());
                })
                .ConfigureLogging((hostCtx, loggingBuilder) =>
                {
                    loggingBuilder.AddConsole();
                    loggingBuilder.AddDebug();
                });

            using(var host = hostBuilder.Build())
            {
                await host.StartAsync(this.CancellationToken);

                var serviceA = host.Services.GetServices<IHostedService>().OfType<SuperSocketServiceA>().FirstOrDefault();
                Assert.NotNull(serviceA);

                var serviceB = host.Services.GetServices<IHostedService>().OfType<SuperSocketServiceB>().FirstOrDefault();
                Assert.NotNull(serviceB);

                Assert.NotNull(serviceA.ServiceProvider.GetService<ISessionContainer>());
                Assert.NotNull(serviceB.ServiceProvider.GetService<ISessionContainer>());
                Assert.NotSame(serviceA.ServiceProvider.GetService<ISessionContainer>(), serviceB.ServiceProvider.GetService<ISessionContainer>());

                var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await client.ConnectAsync(GetDefaultServerEndPoint(), this.CancellationToken);
                
                using (var stream = new NetworkStream(client))
                using (var streamReader = new StreamReader(stream, Utf8Encoding, true))
                using (var streamWriter = new StreamWriter(stream, Utf8Encoding, 1024 * 1024 * 4))
                {
                    var line = await streamReader.ReadLineAsync(this.CancellationToken);
                    Assert.Equal(serverName1, line);
                }

                Assert.NotNull(server1);
                Assert.Same(server1, serviceA);

                client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await client.ConnectAsync(GetAlternativeServerEndPoint(), this.CancellationToken);
                
                using (var stream = new NetworkStream(client))
                using (var streamReader = new StreamReader(stream, Utf8Encoding, true))
                using (var streamWriter = new StreamWriter(stream, Utf8Encoding, 1024 * 1024 * 4))
                {
                    var line = await streamReader.ReadLineAsync(this.CancellationToken);
                    Assert.Equal(serverName2, line);
                }

                Assert.NotNull(server2);
                Assert.Same(server2, serviceB);

                var hostEnv = server1.ServiceProvider.GetService<IHostEnvironment>();
                Assert.NotNull(hostEnv);
                Assert.Equal(actualHostEvn.ContentRootPath, hostEnv.ContentRootPath);

                var hostAppLifetime = server1.ServiceProvider.GetService<IHostApplicationLifetime>();
                Assert.NotNull(hostAppLifetime);
                
                var hostLifetime = server1.ServiceProvider.GetService<IHostLifetime>();
                Assert.NotNull(hostLifetime);

                var hostFromServices = server1.ServiceProvider.GetService<IHost>();
                Assert.NotNull(hostFromServices);

                Assert.NotSame(server1.GetSessionContainer(), server2.GetSessionContainer());

                var loggerFactory0 = host.Services.GetService<ILoggerFactory>();
                var loggerFactory1 = server1.ServiceProvider.GetService<ILoggerFactory>();
                var loggerFactory2 = server2.ServiceProvider.GetService<ILoggerFactory>();

                Assert.Equal(loggerFactory0, loggerFactory1);
                Assert.Equal(loggerFactory1, loggerFactory2);

                var testService0 = host.Services.GetService<MyTestService>();
                testService0.Name = "SameInstance";
                testService0.Version = 1;

                var testService1 = server1.ServiceProvider.GetService<MyTestService>();
                Assert.Equal(testService0.Name, testService1.Name);
                Assert.Equal(1, testService1.Version);
                testService1.Version = 2;
                Assert.Same(server1, server1.ServiceProvider.GetService<IServerInfo>());
                Assert.Same(server1, server1.ServiceProvider.GetService<MyLocalTestService>().Server);

                var testService2 = server2.ServiceProvider.GetService<MyTestService>();
                Assert.Equal(testService0.Name, testService2.Name);
                Assert.Equal(2, testService2.Version);
                Assert.Same(server2, server2.ServiceProvider.GetService<IServerInfo>());
                Assert.Same(server2, server2.ServiceProvider.GetService<MyLocalTestService>().Server);

                await host.StopAsync();
            }
        }

        [Fact]
        public async Task TestMultipleServerHostWithConfigureServerOptions()
        {
            var serverName1 = "TestServer1";
            var serverName2 = "TestServer2";

            var server1 = default(IServer);
            var server2 = default(IServer);

            IHostEnvironment actualHostEvn = null;

            var hostBuilder = MultipleServerHostBuilder.Create()                
                .AddServer<SuperSocketServiceA, TextPackageInfo, LinePipelineFilter>(builder =>
                {
                    builder
                    .UseSessionHandler(async (s) =>
                    {
                        server1 = s.Server as IServer;
                        await s.SendAsync(Utf8Encoding.GetBytes($"{s.Server.Name}\r\n"));
                    })
                    .UseInProcSessionContainer()
                    .ConfigureServices((ctx, services) =>
                    {
                        services.AddSingleton<MyLocalTestService>();
                        services.Configure<ServerOptions>(options =>
                        {
                            options.Name = serverName1;
                            options.Listeners = new List<ListenOptions>
                            {
                                new ListenOptions
                                {
                                    Port = 4040,
                                    Ip = "Any"
                                }
                            };

                        });
                    });
                })
                .AddServer<SuperSocketServiceB, TextPackageInfo, LinePipelineFilter>(builder =>
                {
                    builder
                    .UseSessionHandler(async (s) =>
                    {
                        server2 = s.Server as IServer;
                        await s.SendAsync(Utf8Encoding.GetBytes($"{s.Server.Name}\r\n"));
                    })
                    .UseInProcSessionContainer()
                    .ConfigureServices((ctx, services) =>
                    {
                        services.AddSingleton<MyLocalTestService>();
                        services.Configure<ServerOptions>(options =>
                        {
                            options.Name = serverName2;
                            options.Listeners = new List<ListenOptions>
                            {
                                new ListenOptions
                                {
                                    Port = 4041,
                                    Ip = "Any"
                                }
                            };
                        });
                    });
                })
                .ConfigureLogging((hostCtx, loggingBuilder) =>
                {
                    loggingBuilder.AddConsole();
                    loggingBuilder.AddDebug();
                });

            using (var host = hostBuilder.Build())
            {
                await host.StartAsync();

                var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await client.ConnectAsync(GetDefaultServerEndPoint());

                using (var stream = new NetworkStream(client))
                using (var streamReader = new StreamReader(stream, Utf8Encoding, true))
                using (var streamWriter = new StreamWriter(stream, Utf8Encoding, 1024 * 1024 * 4))
                {
                    var line = await streamReader.ReadLineAsync();
                    Assert.Equal(serverName1, line);
                }

                client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await client.ConnectAsync(GetAlternativeServerEndPoint());

                using (var stream = new NetworkStream(client))
                using (var streamReader = new StreamReader(stream, Utf8Encoding, true))
                using (var streamWriter = new StreamWriter(stream, Utf8Encoding, 1024 * 1024 * 4))
                {
                    var line = await streamReader.ReadLineAsync();
                    Assert.Equal(serverName2, line);
                }

                await host.StopAsync();
            }
        }

        class SuperSocketServiceA : SuperSocketService<TextPackageInfo>
        {
            public SuperSocketServiceA(IServiceProvider serviceProvider, IOptions<ServerOptions> serverOptions)
                : base(serviceProvider, serverOptions)
            {

            }
        }

        class SuperSocketServiceB : SuperSocketService<TextPackageInfo>
        {
            public SuperSocketServiceB(IServiceProvider serviceProvider, IOptions<ServerOptions> serverOptions)
                : base(serviceProvider, serverOptions)
            {
                
            }
        }

        class MyTestService
        {
            public string Name { get; set; }

            public int Version { get; set; } = 0;

            public MyTestService()
            {

            }
        }

        class MyLocalTestService
        {
            public IServerInfo Server { get; private set; }

            public MyLocalTestService(IServerInfo server)
            {
                Server = server;
            }
        }
    }
}