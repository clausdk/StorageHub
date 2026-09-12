using System.Buffers.Binary;
using System.Text;
using StorageHub.Security;
using Xunit;

namespace StorageHub.Security.Tests;

public sealed class SettingsExportEnvelopeTests
{
    private const string Password = "correct horse battery";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes(
        """{"schemaVersion":1,"formatId":"storagehub.settings-export","secretish":"shs_example"}""");

    [Fact]
    public void Protect_then_unprotect_round_trips_the_payload()
    {
        var envelope = SettingsExportEnvelope.Protect(Payload, Password);

        Assert.Equal(Payload, SettingsExportEnvelope.Unprotect(envelope, Password));
    }

    [Fact]
    public void Protecting_the_same_payload_twice_produces_different_bytes()
    {
        // A fresh salt and nonce per file, so two exports of one configuration never match.
        var first = SettingsExportEnvelope.Protect(Payload, Password);
        var second = SettingsExportEnvelope.Protect(Payload, Password);

        Assert.NotEqual(first, second);
        Assert.Equal(Payload, SettingsExportEnvelope.Unprotect(second, Password));
    }

    [Fact]
    public void The_envelope_never_contains_the_payload()
    {
        var envelope = SettingsExportEnvelope.Protect(Payload, Password);

        Assert.DoesNotContain(
            Convert.ToHexString(Payload),
            Convert.ToHexString(envelope),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrong_password_reports_exactly_what_a_damaged_file_reports()
    {
        var envelope = SettingsExportEnvelope.Protect(Payload, Password);
        var damaged = SettingsExportEnvelope.Protect(Payload, Password);
        damaged[^1] ^= 0x5A;

        var wrongPassword = Assert.Throws<SettingsExportCorruptedException>(
            () => SettingsExportEnvelope.Unprotect(envelope, Password + "!"));
        var corrupted = Assert.Throws<SettingsExportCorruptedException>(
            () => SettingsExportEnvelope.Unprotect(damaged, Password));

        Assert.Equal(corrupted.Message, wrongPassword.Message);
        Assert.DoesNotContain("password is", wrongPassword.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Flipping_any_authenticated_header_byte_fails_authentication()
    {
        // The proof that the header, salt and nonce are covered as associated data: a tampered
        // work factor or algorithm id must fail rather than quietly take effect.
        // Sealed once and copied per offset: protecting inside the loop would derive a key eighty
        // times over and dominate the suite's runtime for no extra coverage.
        var sealedEnvelope = SettingsExportEnvelope.Protect(
            Payload, Password, SettingsExportEnvelope.MinimumIterations);

        for (var offset = 0; offset < SettingsExportEnvelope.AuthenticatedHeaderLength; offset++)
        {
            var envelope = sealedEnvelope.ToArray();
            envelope[offset] ^= 0x5A;

            Assert.Throws<SettingsExportCorruptedException>(
                () => SettingsExportEnvelope.Unprotect(envelope, Password));
        }
    }

    [Fact]
    public void Flipping_a_ciphertext_or_tag_byte_fails_authentication()
    {
        var ciphertext = SettingsExportEnvelope.Protect(Payload, Password);
        ciphertext[SettingsExportEnvelope.AuthenticatedHeaderLength] ^= 0x5A;
        var tag = SettingsExportEnvelope.Protect(Payload, Password);
        tag[^1] ^= 0x5A;

        Assert.Throws<SettingsExportCorruptedException>(
            () => SettingsExportEnvelope.Unprotect(ciphertext, Password));
        Assert.Throws<SettingsExportCorruptedException>(
            () => SettingsExportEnvelope.Unprotect(tag, Password));
    }

    [Fact]
    public void A_truncated_envelope_is_rejected()
    {
        var envelope = SettingsExportEnvelope.Protect(Payload, Password);

        Assert.Throws<SettingsExportCorruptedException>(
            () => SettingsExportEnvelope.Unprotect(envelope.AsSpan(0, envelope.Length - 1), Password));
        Assert.Throws<SettingsExportCorruptedException>(
            () => SettingsExportEnvelope.Unprotect(
                envelope.AsSpan(0, SettingsExportEnvelope.FixedHeaderLength), Password));
        Assert.Throws<SettingsExportCorruptedException>(
            () => SettingsExportEnvelope.Unprotect([], Password));
    }

    [Fact]
    public void A_payload_length_that_disagrees_with_the_file_is_rejected()
    {
        var envelope = SettingsExportEnvelope.Protect(Payload, Password);
        BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(32), Payload.Length + 1);

        Assert.Throws<SettingsExportCorruptedException>(
            () => SettingsExportEnvelope.Unprotect(envelope, Password));
    }

    [Theory]
    [InlineData(8, SettingsExportEnvelope.EnvelopeVersion + 1)]
    [InlineData(12, SettingsExportEnvelope.Pbkdf2HmacSha256 + 1)]
    [InlineData(16, SettingsExportEnvelope.MinimumIterations - 1)]
    [InlineData(16, SettingsExportEnvelope.MaximumIterations + 1)]
    [InlineData(20, SettingsExportEnvelope.SaltLength + 1)]
    [InlineData(24, SettingsExportEnvelope.NonceLength + 1)]
    [InlineData(28, SettingsExportEnvelope.TagLength + 1)]
    public void An_unsupported_header_value_is_rejected(int offset, int value)
    {
        // Rejected on the declared value, before key derivation, so a hostile work factor cannot
        // cost anything even though the tag would also have caught it.
        var envelope = SettingsExportEnvelope.Protect(Payload, Password);
        BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(offset), value);

        Assert.Throws<SettingsExportCorruptedException>(
            () => SettingsExportEnvelope.Unprotect(envelope, Password));
    }

    [Fact]
    public void The_recorded_iteration_count_is_the_one_used_on_read()
    {
        // Proves the count travels in the file, so raising the default later leaves older exports
        // readable.
        var envelope = SettingsExportEnvelope.Protect(
            Payload, Password, SettingsExportEnvelope.MinimumIterations);

        Assert.Equal(
            SettingsExportEnvelope.MinimumIterations,
            BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(16)));
        Assert.Equal(Payload, SettingsExportEnvelope.Unprotect(envelope, Password));
    }

    [Fact]
    public void Protect_refuses_an_iteration_count_outside_the_supported_range()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => SettingsExportEnvelope.Protect(
            Payload, Password, SettingsExportEnvelope.MinimumIterations - 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => SettingsExportEnvelope.Protect(
            Payload, Password, SettingsExportEnvelope.MaximumIterations + 1));
    }

    [Fact]
    public void Encrypted_envelopes_are_distinguishable_from_plain_json()
    {
        var envelope = SettingsExportEnvelope.Protect(Payload, Password);

        Assert.True(SettingsExportEnvelope.LooksEncrypted(envelope));
        Assert.False(SettingsExportEnvelope.LooksEncrypted(Payload));
        Assert.False(SettingsExportEnvelope.LooksEncrypted([]));
        Assert.False(SettingsExportEnvelope.LooksEncrypted("SHEXP"u8));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("       ")]
    [InlineData("short")]
    public void An_unusable_password_is_refused_before_anything_is_written(string? password)
    {
        Assert.NotNull(SettingsExportEnvelope.ValidatePassword(password));
        _ = Assert.Throws<ArgumentException>(() => SettingsExportEnvelope.Protect(Payload, password!));
    }

    [Fact]
    public void An_over_long_password_is_refused()
    {
        var password = new string('p', SettingsExportEnvelope.MaximumPasswordLength + 1);

        Assert.NotNull(SettingsExportEnvelope.ValidatePassword(password));
        _ = Assert.Throws<ArgumentException>(() => SettingsExportEnvelope.Protect(Payload, password));
        Assert.Null(SettingsExportEnvelope.ValidatePassword(
            new string('p', SettingsExportEnvelope.MaximumPasswordLength)));
    }

    [Fact]
    public void A_payload_larger_than_the_cap_is_refused()
    {
        var payload = new byte[SettingsExportEnvelope.MaximumPayloadBytes + 1];

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => SettingsExportEnvelope.Protect(payload, Password));
    }

    [Fact]
    public void An_empty_payload_round_trips()
    {
        var envelope = SettingsExportEnvelope.Protect([], Password);

        Assert.Empty(SettingsExportEnvelope.Unprotect(envelope, Password));
    }

    [Fact]
    public void A_non_ascii_password_round_trips()
    {
        // UTF-8 is pinned into the format, so these must survive unchanged.
        const string Accented = "kodeord-æøå-Ünicode";

        var envelope = SettingsExportEnvelope.Protect(Payload, Accented);

        Assert.Equal(Payload, SettingsExportEnvelope.Unprotect(envelope, Accented));
    }
}
