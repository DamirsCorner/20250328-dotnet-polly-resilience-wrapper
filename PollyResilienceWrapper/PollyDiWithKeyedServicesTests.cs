using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.RateLimiting;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PollyResilienceWrapper;

public class PollyDiWithKeyedServicesTests
{
    private const string RATE_LIMITING_PIPELINE_KEY = "rate-limiting-pipeline";
    private const int MAX_REQUESTS_PER_SECOND = 5;

    public class MyService(
        HttpClient httpClient,
        [FromKeyedServices(RATE_LIMITING_PIPELINE_KEY)] ResiliencePipeline rateLimitingPipeline
    )
    {
        public async Task<string> GetAsync()
        {
            return await rateLimitingPipeline.ExecuteAsync(async cancellationToken =>
                await httpClient.GetStringAsync("", cancellationToken)
            );
        }
    }

    private WireMockServer server;
    private ServiceProvider serviceProvider;

    [SetUp]
    public void Setup()
    {
        server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/").UsingGet())
            .RespondWith(Response.Create().WithBody("Hello world!"));

        var services = new ServiceCollection();
        services.AddHttpClient<MyService>(client =>
        {
            client.BaseAddress = new Uri(server.Urls[0]);
        });
        services.AddResiliencePipeline(
            RATE_LIMITING_PIPELINE_KEY,
            (builder, context) =>
            {
                builder.AddRateLimiter(
                    new SlidingWindowRateLimiter(
                        new SlidingWindowRateLimiterOptions
                        {
                            PermitLimit = MAX_REQUESTS_PER_SECOND,
                            SegmentsPerWindow = 10,
                            Window = TimeSpan.FromSeconds(1)
                        }
                    )
                );
            }
        );
        serviceProvider = services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        server.Stop();
        server.Dispose();

        serviceProvider.Dispose();
    }

    [Test]
    public async Task InjectsPipelineIntoService()
    {
        var service = serviceProvider.GetRequiredService<MyService>();

        var tasks = Enumerable.Range(0, 5).Select(_ => service.GetAsync()).ToArray();
        await Task.WhenAll(tasks).ShouldNotThrowAsync();

        await service.GetAsync().ShouldThrowAsync<RateLimiterRejectedException>();

        await Task.Delay(TimeSpan.FromSeconds(1));
        await service.GetAsync().ShouldNotThrowAsync();
    }
}
