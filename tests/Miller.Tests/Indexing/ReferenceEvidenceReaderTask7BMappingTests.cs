using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class ReferenceEvidenceReaderTask7BMappingTests
{
    [Fact]
    public void ReverseMapsOnlyToInboundEvidenceArms()
    {
        Assert.Equal(
            [
                new Task7BArm(ReferenceEvidenceReadPhase.InboundExact, "inbound-exact"),
                new Task7BArm(ReferenceEvidenceReadPhase.InboundFallback, "inbound-fallback"),
            ],
            Task7BArmMapping.ForDirection("reverse"));
    }

    [Fact]
    public void ForwardMapsOnlyToOutgoingEvidenceArms()
    {
        Assert.Equal(
            [
                new Task7BArm(ReferenceEvidenceReadPhase.OutgoingExact, "outgoing-exact"),
                new Task7BArm(ReferenceEvidenceReadPhase.OutgoingFallback, "outgoing-fallback"),
            ],
            Task7BArmMapping.ForDirection("forward"));
    }
}
