// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Common;
using GhostShell.Core.Models;
using Xunit;

namespace GhostShell.Tests.Models;

/// <summary>
/// JSON shape is the on-the-wire format: snapshots persist via this
/// helper, packs export to disk via this helper, the import path
/// reads via this helper. Round-trip stability is a hard contract;
/// these tests keep it from drifting.
/// </summary>
public class SessionPayloadJsonTests
{
    [Fact]
    public void RoundTrip_PreservesAllCookieFields()
    {
        var original = new SessionPayload
        {
            Cookies =
            [
                new CookieEntry
                {
                    Name           = "NID",
                    Value          = "511=abc:def:ghi",
                    Domain         = ".google.com",
                    Path           = "/search",
                    Secure         = true,
                    HttpOnly       = true,
                    SameSite       = "None",
                    ExpiresUnixSec = 1_900_000_000,
                },
            ],
            Storage =
            [
                new StorageEntry
                {
                    Origin         = "https://www.google.com",
                    LocalStorage   = new Dictionary<string, string>
                    {
                        ["theme"]      = "dark",
                        ["lang"]       = "en",
                        ["complex"]    = "{\"nested\":true}",
                    },
                    SessionStorage = new Dictionary<string, string>
                    {
                        ["transient"] = "x",
                    },
                },
            ],
        };

        var json    = SessionPayloadJson.SerializePayload(original);
        var decoded = SessionPayloadJson.DeserializePayload(json)!;

        Assert.Single(decoded.Cookies);
        var c = decoded.Cookies[0];
        Assert.Equal("NID",                  c.Name);
        Assert.Equal("511=abc:def:ghi",      c.Value);
        Assert.Equal(".google.com",          c.Domain);
        Assert.Equal("/search",              c.Path);
        Assert.True (c.Secure);
        Assert.True (c.HttpOnly);
        Assert.Equal("None",                 c.SameSite);
        Assert.Equal(1_900_000_000L,         c.ExpiresUnixSec);

        Assert.Single(decoded.Storage);
        var s = decoded.Storage[0];
        Assert.Equal("https://www.google.com", s.Origin);
        Assert.Equal("dark",                   s.LocalStorage["theme"]);
        Assert.Equal("en",                     s.LocalStorage["lang"]);
        Assert.Equal("{\"nested\":true}",      s.LocalStorage["complex"]);
        Assert.Equal("x",                      s.SessionStorage["transient"]);
    }

    [Fact]
    public void Deserialize_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(SessionPayloadJson.DeserializePayload(""));
        Assert.Null(SessionPayloadJson.DeserializePayload("   "));
    }

    [Fact]
    public void DeserializeCookies_HandlesMissingOptionalFields()
    {
        // A minimal cookie has only name, value, domain. Defaults
        // for path/secure/httpOnly should kick in without loss.
        var json = """[{"name":"x","value":"1","domain":".y.com"}]""";
        var list = SessionPayloadJson.DeserializeCookies(json);

        Assert.Single(list);
        Assert.Equal("/", list[0].Path);
        Assert.False (list[0].Secure);
        Assert.False (list[0].HttpOnly);
        Assert.Null  (list[0].SameSite);
        Assert.Null  (list[0].ExpiresUnixSec);
    }

    [Fact]
    public void DeserializeCookies_EmptyOrNullJson_ReturnsEmpty()
    {
        Assert.Empty(SessionPayloadJson.DeserializeCookies(""));
        Assert.Empty(SessionPayloadJson.DeserializeCookies("   "));
        Assert.Empty(SessionPayloadJson.DeserializeCookies("[]"));
    }

    [Fact]
    public void IsEmpty_FlagsFreshPayload()
    {
        Assert.True(SessionPayload.Empty.IsEmpty);
        Assert.True(new SessionPayload().IsEmpty);
        Assert.False(new SessionPayload
        {
            Cookies = [ new CookieEntry { Name = "x", Value = "1", Domain = ".y.com" } ],
        }.IsEmpty);
    }
}
