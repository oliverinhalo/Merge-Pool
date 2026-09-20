using MergePool.Ipc.Protocol;
using Xunit;

namespace MergePool.Ipc.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void A_client_speaking_the_same_version_negotiates_it() =>
        Assert.Equal(ProtocolVersion.Current, ProtocolVersion.Negotiate(ProtocolVersion.Current, ProtocolVersion.Current));

    [Fact]
    public void An_old_client_still_gets_a_version_from_a_newer_service()
    {
        // The client only knows the oldest supported version; a newer service must still serve it.
        var oldest = ProtocolVersion.MinimumSupported;
        var negotiated = ProtocolVersion.Negotiate(clientMinimum: oldest, clientMaximum: oldest);

        Assert.Equal(oldest, negotiated);
    }

    [Fact]
    public void A_newer_client_falls_back_to_what_the_service_speaks()
    {
        var negotiated = ProtocolVersion.Negotiate(clientMinimum: 1, clientMaximum: ProtocolVersion.Current + 5);

        Assert.Equal(ProtocolVersion.Current, negotiated);
    }

    [Fact]
    public void A_client_that_needs_a_future_version_gets_nothing()
    {
        var negotiated = ProtocolVersion.Negotiate(
            clientMinimum: ProtocolVersion.Current + 1,
            clientMaximum: ProtocolVersion.Current + 3);

        Assert.Null(negotiated);
    }

    [Fact]
    public void A_client_too_old_for_the_service_gets_nothing()
    {
        // Anything strictly below the supported floor cannot be served, whatever the floor is.
        var tooOld = ProtocolVersion.MinimumSupported - 1;

        Assert.Null(ProtocolVersion.Negotiate(tooOld - 1, tooOld));
    }

    [Fact]
    public void The_supported_range_is_coherent() =>
        Assert.True(ProtocolVersion.MinimumSupported <= ProtocolVersion.Current);
}
