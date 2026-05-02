// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Extensions;

/// <summary>
/// Phase 27 — curated catalog of popular Chrome extensions shipped with
/// the app. The Chrome Web Store has no public search API, so we ship
/// a list of well-known extensions that the user can install in one
/// click. Custom extensions can still be installed via "paste CWS URL"
/// flow which goes straight to <see cref="IExtensionService.InstallFromStoreAsync"/>.
///
/// Each entry's ExtId is the 32-char ID from the CWS URL. The download
/// path is <c>https://clients2.google.com/service/update2/crx?...x=id%3D{id}</c>
/// which serves the latest .crx.
/// </summary>
public static class CuratedExtensionsCatalog
{
    /// <summary>Static list — built once, reused across all reads.</summary>
    public static IReadOnlyList<ExtensionStoreEntry> Entries { get; } = new[]
    {
        new ExtensionStoreEntry
        {
            ExtId = "cjpalhdlnbpafiamejdnhcphjbkeiagm",
            Name = "uBlock Origin",
            Description = "Efficient wide-spectrum content blocker. Easy on memory and CPU footprint.",
            Author = "Raymond Hill (gorhill)",
            Rating = 4.7, Users = 39_000_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "ddkjiahejlhfcafbddmgiahcphecmpfh",
            Name = "uBlock Origin Lite",
            Description = "Permission-less, declarativeNetRequest-based content blocker (manifest v3).",
            Author = "Raymond Hill (gorhill)",
            Rating = 4.5, Users = 1_500_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "nngceckbapebfimnlniiiahkandclblb",
            Name = "Bitwarden — Free Password Manager",
            Description = "A secure and free password manager for all of your devices.",
            Author = "Bitwarden Inc.",
            Rating = 4.7, Users = 2_500_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "hdokiejnpimakedhajhdlcegeplioahd",
            Name = "LastPass: Free Password Manager",
            Description = "An award-winning password manager. Save your passwords and log in securely.",
            Author = "LastPass",
            Rating = 4.4, Users = 9_000_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "nkbihfbeogaeaoehlefnkodbefgpgknn",
            Name = "MetaMask",
            Description = "A crypto wallet & gateway to blockchain apps.",
            Author = "MetaMask",
            Rating = 3.5, Users = 10_000_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "mnjggcdmjocbbbhaepdhchncahnbgone",
            Name = "Phantom",
            Description = "Solana / Ethereum / Polygon wallet for Web3.",
            Author = "Phantom",
            Rating = 4.5, Users = 3_000_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "mcohilncbfahbmgdjkbpemcciiolgcge",
            Name = "OKX Wallet",
            Description = "Multi-chain crypto wallet with built-in DEX, NFT marketplace, and DeFi tools.",
            Author = "OKX",
            Rating = 4.6, Users = 2_500_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "egjidjbpglichdcondbcbdnbeeppgdph",
            Name = "Trust Wallet",
            Description = "Multi-coin crypto wallet supporting hundreds of blockchains.",
            Author = "Trust Wallet",
            Rating = 4.6, Users = 1_200_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "hnfanknocfeofbddgcijnmhnfnkdnaad",
            Name = "Coinbase Wallet",
            Description = "Self-custody crypto wallet — store, swap, and manage NFTs.",
            Author = "Coinbase",
            Rating = 4.0, Users = 1_500_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "acmacodkjbdgmoleebolmdjonilkdbch",
            Name = "Rabby Wallet",
            Description = "Open-source multi-chain Ethereum wallet, transaction simulator built in.",
            Author = "DeBank",
            Rating = 4.7, Users = 700_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "dmkamcknogkgcdfhhbddcghachkejeap",
            Name = "Keplr",
            Description = "Browser wallet for Cosmos ecosystem chains (Osmosis, Sei, Celestia, etc.).",
            Author = "Chainapsis",
            Rating = 4.5, Users = 600_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "ibnejdfjmmkpcnlpebklmnkoeoihofec",
            Name = "TronLink",
            Description = "Tron blockchain wallet & dApp browser.",
            Author = "TronLink",
            Rating = 4.0, Users = 800_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "omaabbefbmiijedngplfjmnooppbclkk",
            Name = "Tonkeeper",
            Description = "TON blockchain wallet — transfer, stake, and use Telegram-native dApps.",
            Author = "Tonkeeper",
            Rating = 4.4, Users = 200_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "agoakfejjabomempkjlepdflaleeobhb",
            Name = "Core",
            Description = "Built by Avalanche — multi-chain wallet with bridges + NFT support.",
            Author = "Ava Labs",
            Rating = 4.4, Users = 500_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "cgeeodpfagjceefieflmdfphplkenlfk",
            Name = "Ethos",
            Description = "Sui / Aptos wallet with NFT gallery + dApp connect.",
            Author = "Ethos Wallet",
            Rating = 4.5, Users = 100_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "ejbalbakoplchlghecdalmeeeajnimhm",
            Name = "MetaMask Flask",
            Description = "Developer build of MetaMask — early-access features, breakage expected.",
            Author = "MetaMask",
            Rating = 3.9, Users = 80_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "fhilaheimglignddkjgofkcbgekhenbh",
            Name = "Oxygen",
            Description = "Solana NFT + DeFi wallet.",
            Author = "Oxygen Labs",
            Rating = 4.0, Users = 70_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "fhbjgbiflinjbdggehcddcbncdddomop",
            Name = "Postman Interceptor",
            Description = "Capture cookies / requests directly into Postman.",
            Author = "Postman Inc.",
            Rating = 3.5, Users = 600_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "fihnjjcciajhdojfnbdddfaoknhalnja",
            Name = "I don't care about cookies",
            Description = "Get rid of cookie warnings from almost all websites.",
            Author = "Daniel Kladnik",
            Rating = 4.3, Users = 1_700_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "edibdbjcniadpccecjdfdjjppcpchdlm",
            Name = "I still don't care about cookies",
            Description = "Open-source fork of the original — same behaviour, no telemetry.",
            Author = "OhMyGuus",
            Rating = 4.5, Users = 700_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "gighmmpiobklfepjocnamgkkbiglidom",
            Name = "AdBlock — best ad blocker",
            Description = "Block ads and pop-ups on YouTube, Facebook, Twitch, and your favorite websites.",
            Author = "getadblock.com",
            Rating = 4.5, Users = 60_000_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "cfhdojbkjhnklbpkdaibdccddilifddb",
            Name = "Adblock Plus — free ad blocker",
            Description = "The world's #1 free ad blocker.",
            Author = "eyeo GmbH",
            Rating = 4.4, Users = 50_000_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "bkdgflcldnnnapblkhphbgpggdiikppg",
            Name = "DuckDuckGo Privacy Essentials",
            Description = "Privacy, simplified. Block trackers, force HTTPS, hide your search history.",
            Author = "DuckDuckGo",
            Rating = 4.4, Users = 5_000_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "pkehgijcmpdhfbdbbnkijodmdjhbjlgp",
            Name = "Privacy Badger",
            Description = "Automatically learns to block invisible trackers.",
            Author = "EFF Technologists",
            Rating = 4.5, Users = 1_500_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "ldpochfccmkkmhdbclfhpagapcfdljkj",
            Name = "Decentraleyes",
            Description = "Protects you against tracking through 'free', centralized, content delivery.",
            Author = "Thomas Rientjes",
            Rating = 4.6, Users = 200_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "khaoiebndkojlmppeemjhbpbandiljpe",
            Name = "Free VPN for Chrome — VPN Proxy VeePN",
            Description = "Free VPN proxy with strong encryption.",
            Author = "VeePN",
            Rating = 4.7, Users = 4_000_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "fdcgdnkidjaadafnichfpabhfomcebme",
            Name = "ZenMate VPN — Best Free VPN",
            Description = "Encrypts your traffic and hides your IP.",
            Author = "ZenGuard GmbH",
            Rating = 4.5, Users = 2_000_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "bgnkhhnnamicmpeenaelnjfhikgbkllg",
            Name = "AdGuard AdBlocker",
            Description = "Blocks all ads. Best ad blocker for browsing without distractions.",
            Author = "AdGuard Software Ltd",
            Rating = 4.7, Users = 11_000_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "lifbcibllhkdhoafpjfnlhfpfgnpldfl",
            Name = "Skip Ads on YouTube",
            Description = "Auto-skips skippable YouTube ads.",
            Author = "Skip Ads",
            Rating = 4.0, Users = 600_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "kdfieneakcjfaiglcfcgkidlkmlijjnh",
            Name = "Loom — Free Screen Recorder & Screen Capture",
            Description = "Free screen recorder for Chrome.",
            Author = "loom.com",
            Rating = 4.7, Users = 8_000_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "cflnaagajbnkcgalmpfnjnmnknfhdmbn",
            Name = "Click&Clean",
            Description = "Cleans browsing history, cookies, downloads + more in one click.",
            Author = "Click&Clean",
            Rating = 4.4, Users = 1_700_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "ofpnmcalabcbjgholdjcjblkibolbppb",
            Name = "Tampermonkey",
            Description = "The world's most popular userscript manager.",
            Author = "Jan Biniok",
            Rating = 4.7, Users = 10_000_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "hdeoamhgghbjjadkglddiacipljkhjbi",
            Name = "Violentmonkey",
            Description = "Open-source userscript manager.",
            Author = "violentmonkey.github.io",
            Rating = 4.7, Users = 600_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "fmkadmapgofadopljbjfkapdkoienihi",
            Name = "React Developer Tools",
            Description = "Adds React debugging tools to Chrome DevTools.",
            Author = "Facebook",
            Rating = 4.0, Users = 4_000_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "lmhkpmbekcpmknklioeibfkpmmfibljd",
            Name = "Redux DevTools",
            Description = "Redux DevTools for debugging application's state changes.",
            Author = "Mihail Diordiev",
            Rating = 4.7, Users = 2_000_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "nhdogjmejiglipccpnnnanhbledajbpd",
            Name = "Vue.js devtools",
            Description = "Chrome devtools extension for debugging Vue.js applications.",
            Author = "vuejs.org",
            Rating = 4.7, Users = 1_000_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "amknoiejhlmhancpahfcfcfhllgkpbld",
            Name = "Honey: Automatic Coupons & Cash Back",
            Description = "Save money + earn cash back with automatic coupons.",
            Author = "PayPal",
            Rating = 4.6, Users = 17_000_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "nmmhkkegccagdldgiimedpiccmgmieda",
            Name = "Google Wallet",
            Description = "Google Pay payment method picker for Chrome.",
            Author = "Google",
            Rating = 4.0, Users = 4_000_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "kbfnbcaeplbcioakkpcpgfkobkghlhen",
            Name = "Grammarly: AI Writing & Grammar Checker",
            Description = "Improve your writing with the AI-powered grammar checker.",
            Author = "Grammarly Inc.",
            Rating = 4.6, Users = 40_000_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "aapbdbdomjkkjkaonfhkkikfgjllcleb",
            Name = "Google Translate",
            Description = "View translations easily as you browse the web.",
            Author = "Google",
            Rating = 4.6, Users = 10_000_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "cofdbpoegempjloogbagkncekinflcnj",
            Name = "DeepL Translate",
            Description = "Translate while you read and write — DeepL Translator.",
            Author = "DeepL",
            Rating = 4.7, Users = 3_000_000,
        },
        new ExtensionStoreEntry
        {
            ExtId = "dhdgffkkebhmkfjojejmpbldmpobfkfo",
            Name = "Tampermonkey BETA",
            Description = "Beta channel of the userscript manager.",
            Author = "Jan Biniok",
            Rating = 4.7, Users = 1_000_000,
        },
    };

    public static IEnumerable<ExtensionStoreEntry> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Entries;
        var q = query.Trim();
        return Entries.Where(e =>
            (e.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
            (e.Description ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
            (e.Author ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
    }
}
