using WindowsGSH.Core.Network.Upnp;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class UpnpGatewayMutationTests
{
    private const string ServiceType = "urn:schemas-upnp-org:service:WANIPConnection:1";

    [Fact]
    public async Task Add_sends_the_complete_normalized_soap_argument_set()
    {
        string? action = null;
        IReadOnlyList<(string Name, string Value)>? sent = null;
        var gateway = Gateway((_, _, actionName, arguments, _) =>
        {
            action = actionName;
            sent = arguments;
            return Task.FromResult(UpnpSoapResponse.SuccessResponse(new Dictionary<string, string>(), "AddPortMappingResponse", ServiceType));
        });

        var result = await gateway.AddPortMappingAsync(
            new("", 7777, "udp", 7778, "192.168.1.50", "WindowsGSH:test", 3600));

        Assert.True(result.Succeeded);
        Assert.Equal("AddPortMapping", action);
        Assert.Contains(("NewProtocol", "UDP"), sent!);
        Assert.Contains(("NewEnabled", "1"), sent!);
        Assert.Contains(("NewLeaseDuration", "3600"), sent!);
    }

    [Fact]
    public async Task Delete_sends_only_the_mapping_identity()
    {
        string? action = null;
        IReadOnlyList<(string Name, string Value)>? sent = null;
        var gateway = Gateway((_, _, actionName, arguments, _) =>
        {
            action = actionName;
            sent = arguments;
            return Task.FromResult(UpnpSoapResponse.SuccessResponse(new Dictionary<string, string>(), "DeletePortMappingResponse", ServiceType));
        });

        var result = await gateway.DeletePortMappingAsync("", 7777, "udp");

        Assert.True(result.Succeeded);
        Assert.Equal("DeletePortMapping", action);
        Assert.Equal(3, sent!.Count);
        Assert.Contains(("NewProtocol", "UDP"), sent);
    }

    [Fact]
    public async Task Add_rejects_a_wellformed_but_unrecognized_success_body()
    {
        // Regression guard: ParseSoapResponse treats any non-Fault SOAP body child as "success"
        // without checking which action it actually is. A gateway that answers with a different
        // (or missing/malformed) response element must not be reported as "the mapping was
        // created" - CreateAsync would otherwise persist ownership for a mapping that was never
        // actually made.
        var gateway = Gateway((_, _, _, _, _) =>
            Task.FromResult(UpnpSoapResponse.SuccessResponse(new Dictionary<string, string>(), "SomeOtherActionResponse", ServiceType)));

        var result = await gateway.AddPortMappingAsync(
            new("", 7777, "udp", 7778, "192.168.1.50", "WindowsGSH:test", 3600));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Add_rejects_the_expected_response_name_in_an_unrelated_namespace()
    {
        var gateway = Gateway((_, _, _, _, _) =>
            Task.FromResult(UpnpSoapResponse.SuccessResponse(
                new Dictionary<string, string>(),
                "AddPortMappingResponse",
                "urn:unrelated-service")));

        var result = await gateway.AddPortMappingAsync(
            new("", 7777, "udp", 7778, "192.168.1.50", "WindowsGSH:test", 3600));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Add_honors_cancellation_requested_before_dispatch()
    {
        var invoked = false;
        var gateway = Gateway((_, _, _, _, _) =>
        {
            invoked = true;
            return Task.FromResult(UpnpSoapResponse.SuccessResponse(new Dictionary<string, string>(), "AddPortMappingResponse", ServiceType));
        });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => gateway.AddPortMappingAsync(
            new("", 7777, "udp", 7778, "192.168.1.50", "WindowsGSH:test", 3600), cts.Token));
        Assert.False(invoked);
    }

    [Fact]
    public async Task Add_does_not_cancel_once_the_request_has_been_dispatched()
    {
        // Regression guard: a caller cancelling while the router is processing the request (or
        // its response is being read) must not lose the result. The router may already have
        // created the mapping - throwing here instead of returning a real outcome would leave
        // UpnpPortMappingService.CreateAsync unable to either register ownership or roll back a
        // mapping that may genuinely now exist on the router.
        using var cts = new CancellationTokenSource();
        var gateway = Gateway((_, _, _, _, cancellationToken) =>
        {
            // Simulate cancellation arriving while the SOAP call is in flight - the invoker
            // receives CancellationToken.None regardless, so it can never observe this itself.
            cts.Cancel();
            Assert.Equal(CancellationToken.None, cancellationToken);
            return Task.FromResult(UpnpSoapResponse.SuccessResponse(new Dictionary<string, string>(), "AddPortMappingResponse", ServiceType));
        });

        var result = await gateway.AddPortMappingAsync(
            new("", 7777, "udp", 7778, "192.168.1.50", "WindowsGSH:test", 3600), cts.Token);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Mutation_preserves_a_router_fault_as_a_failed_result()
    {
        var gateway = Gateway((_, _, _, _, _) =>
            Task.FromResult(UpnpSoapResponse.FaultResponse(718, "ConflictInMappingEntry")));

        var result = await gateway.DeletePortMappingAsync("", 7777, "UDP");

        Assert.Equal(UpnpSoapOutcome.Fault, result.Outcome);
        Assert.Contains("718", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "UDP", "192.168.1.50")]
    [InlineData(65536, "UDP", "192.168.1.50")]
    [InlineData(7777, "SCTP", "192.168.1.50")]
    [InlineData(7777, "UDP", "8.8.8.8")]
    public async Task Add_rejects_unsafe_or_invalid_arguments_before_network_io(
        int externalPort,
        string protocol,
        string internalClient)
    {
        var invoked = false;
        var gateway = Gateway((_, _, _, _, _) =>
        {
            invoked = true;
            return Task.FromResult(UpnpSoapResponse.SuccessResponse(new Dictionary<string, string>()));
        });

        await Assert.ThrowsAsync<ArgumentException>(() => gateway.AddPortMappingAsync(
            new("", externalPort, protocol, 7777, internalClient, "WindowsGSH:test", 3600)));
        Assert.False(invoked);
    }

    private static UpnpGateway Gateway(
        Func<Uri, string, string, IReadOnlyList<(string Name, string Value)>, CancellationToken, Task<UpnpSoapResponse>> invoker) =>
        new(
            new UpnpGatewayDescriptor(
                new Uri("http://192.168.1.1/root.xml"), "Router", null, null,
                "urn:schemas-upnp-org:device:InternetGatewayDevice:1", ServiceType,
                new Uri("http://192.168.1.1/control"), null, "uuid:gateway"),
            invoker);
}
