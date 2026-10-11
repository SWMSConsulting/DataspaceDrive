using DataspaceDrive.Core.Shares;

namespace DataspaceDrive.Core.Tests.Shares;

public class DeliveryTokenServiceTests
{
    private static readonly byte[] KeyV1 = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] KeyV2 = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();

    [Theory]
    // Vektoren aus der Python-Implementierung des Spikes (spike/runner/spike_runner.py, delivery_token).
    [InlineData("spike-vector", "v1.UsX62AMvm6qN5-jDr5eMnZe7YDXhJXvbfFrOygyVdtg")]
    [InlineData("0192f3a1-7c2e-7e44-9a1b-5f0d2c3b4a59", "v1.KxoH_vfJfQEdMY53WA-JAceQNszm7pq3WCmjagFy53s")]
    public void Create_matches_spike_token_format(string deliveryId, string expected)
    {
        var service = new DeliveryTokenService(new Dictionary<int, byte[]> { [1] = KeyV1 }, 1);

        Assert.Equal(expected, service.Create(deliveryId));
    }

    [Fact]
    public void Verify_accepts_own_token_and_rejects_other_delivery()
    {
        var service = new DeliveryTokenService(new Dictionary<int, byte[]> { [1] = KeyV1 }, 1);
        var token = service.Create("delivery-a");

        Assert.True(service.Verify("delivery-a", token));
        Assert.False(service.Verify("delivery-b", token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1.")]
    [InlineData("v9.UsX62AMvm6qN5-jDr5eMnZe7YDXhJXvbfFrOygyVdtg")]
    [InlineData("x1.UsX62AMvm6qN5-jDr5eMnZe7YDXhJXvbfFrOygyVdtg")]
    [InlineData("v1.UsX62AMvm6qN5-jDr5eMnZe7YDXhJXvbfFrOygyVdtG")]
    public void Verify_rejects_malformed_or_tampered_tokens(string? token)
    {
        var service = new DeliveryTokenService(new Dictionary<int, byte[]> { [1] = KeyV1 }, 1);

        Assert.False(service.Verify("spike-vector", token));
    }

    [Fact]
    public void Rotation_keeps_tokens_of_older_key_versions_valid()
    {
        var before = new DeliveryTokenService(new Dictionary<int, byte[]> { [1] = KeyV1 }, 1);
        var oldToken = before.Create("delivery-a");
        var after = new DeliveryTokenService(new Dictionary<int, byte[]> { [1] = KeyV1, [2] = KeyV2 }, 2);

        Assert.True(after.Verify("delivery-a", oldToken));
        Assert.StartsWith("v2.", after.Create("delivery-a"));
    }

    [Fact]
    public void Short_keys_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new DeliveryTokenService(new Dictionary<int, byte[]> { [1] = new byte[16] }, 1));
    }

    [Fact]
    public void Missing_current_version_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new DeliveryTokenService(new Dictionary<int, byte[]> { [1] = KeyV1 }, 2));
    }
}
