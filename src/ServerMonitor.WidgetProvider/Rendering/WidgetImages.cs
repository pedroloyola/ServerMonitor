namespace ServerMonitor.WidgetProvider.Rendering;

/// <summary>
/// UI.9 C3 — the ONLY images a widget card may reference (Vigil C0 debrief §2/§3). The board renders only
/// compile-time constant <c>data:</c> URIs (<c>ms-appx:///Public</c> does not render). So:
/// <list type="bullet">
/// <item>every image is a literal <c>const string</c> here, prefix <c>data:image/png;base64,</c>;</item>
/// <item>templates reference these constants literally and pick one with <c>$when</c>, never through a
/// <c>${…}</c> binding;</item>
/// <item>no byte is generated at runtime, so nothing is derived from snapshot numbers.</item>
/// </list>
/// The PNGs carry only IHDR/IDAT/IEND, each ≤ 4 KB (tests). Bars are 1×1 colour fills in Prism's C0
/// palette (Disk light <c>#A86A1F</c>). The empty-state icon is Hugeicons ServerStack01 (Figma 112:11797)
/// at 2× for 40 px. No SVG: the SVG allowlist forbids <c>http</c>, which the SVG namespace needs (C3 DV-1).
/// Changing any byte here needs Vigil's byte review.
/// </summary>
public static class WidgetImages
{
    /// <summary>1x1 bar fill #B69AF8. PNG 69 B, sha256 c00d606281a49c950a43b2c092bd25d1946a127a47b4b2776d17d6b6e156dde0.</summary>
    public const string BarCpuDark = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR42mPYNusHAARSAkmSaZP4AAAAAElFTkSuQmCC";

    /// <summary>1x1 bar fill #7DB8FF. PNG 69 B, sha256 e443a7303643862cd5c78c9abdeaa3ee6078f4ce6c5fec93643a0fe17a2ee47a.</summary>
    public const string BarRamDark = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR42mOo3fEfAAPqAjWfcoBqAAAAAElFTkSuQmCC";

    /// <summary>1x1 bar fill #FFC16E. PNG 69 B, sha256 5766a9df74887d4b13ae166127a85a8b54205da277512e9e40e7879505b2376a.</summary>
    public const string BarDiskDark = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR42mP4fzAPAATxAi8cMjobAAAAAElFTkSuQmCC";

    /// <summary>1x1 bar fill #ADADAD. PNG 69 B, sha256 90b9056d40895840fea73c3f1308fb80fd41cd365f16e5d2fee2b68f3ff92a9b.</summary>
    public const string BarStaleDark = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR42mNYu3YtAAQSAghla87WAAAAAElFTkSuQmCC";

    /// <summary>1x1 bar fill #484848. PNG 69 B, sha256 757e0ad97e708fbe4bcec1ec0b25336aa3b8ac365a043aaed7a6ec0a57de753d.</summary>
    public const string BarTrackDark = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR42mPw8PAAAAG0ANk33aiZAAAAAElFTkSuQmCC";

    /// <summary>1x1 bar fill #8668CA. PNG 69 B, sha256 11db1bde5c158477cc2c38a08a862daf14e98c9be047e725f4ad80211b362ccd.</summary>
    public const string BarCpuLight = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR42mNoyzgFAAMwAbkplGwFAAAAAElFTkSuQmCC";

    /// <summary>1x1 bar fill #427EC5. PNG 69 B, sha256 005ba23f82ed44b2b58ca5818f873a971fdd8d55abd5c0ea627b4a9e2d4f8991.</summary>
    public const string BarRamLight = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR42mNwqjsKAAKLAYYlot+oAAAAAElFTkSuQmCC";

    /// <summary>1x1 bar fill #A86A1F. PNG 69 B, sha256 2962c28ed05a59af5c88a87f6123d8fb67d6e188b290a5d3cf76f1c2768ae4db.</summary>
    public const string BarDiskLight = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR42mNYkSUPAALvATIg0tJxAAAAAElFTkSuQmCC";

    /// <summary>1x1 bar fill #626262. PNG 69 B, sha256 b6d99b7eded3bd6e490c047aca8ff2c3e51ab45cb5eddb2677beabf4e1bad027.</summary>
    public const string BarStaleLight = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR42mNISkoCAAJQASe12oheAAAAAElFTkSuQmCC";

    /// <summary>1x1 bar fill #E8E8E8. PNG 69 B, sha256 86e0bbc0487710181ab5d050f65bb5529a84cbc09147f54f02ddd772eb71f8f2.</summary>
    public const string BarTrackLight = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR42mN48eIFAAV0ArnnPxTJAAAAAElFTkSuQmCC";

    /// <summary>ServerStack01 80x80 (40 px @2x), stroke #F5F5F5. PNG 1173 B, sha256 4a4bfcaf728ea7a638f58805a756fff922cc92963893eeb3a3ab89ca919c78eb.</summary>
    public const string EmptyIconDark = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFAAAABQCAYAAACOEfKtAAAEXElEQVR42u1cfWhNYRi/CkmNmthipnwU5WOWyeyDma9/hJWmfJus+NNfWJjWzcYyEoqIQiH+Mh8z2xhbsWFYaaSVsoQx92Mrdf2eda4eh+Z9zzade9/nV0/ndrrvOef3u8/7vM953ve9Ho9AIBAIBAKBQCAQCExAKBQaHgwGx/l8vuk4ZsDmR6hlEIdAIJBInPpTsFG40SG/318HC0WxBWGPiCssrk/EwwU342LtUS7cHwbOX2AbeiveqR5uQngLe4gbVUei0bNbHPw98DzhSDzEhVW2X6QGMSO7s7NzErp0TBTG9RjiBlsIrvc5d2ix0knMa2fiHTRtoCTOjH87NBmp03W3scavTc02rO4d9sStjmIfBNxpsIC7mYDHdRqWs4bLTBUQsS+H6XBdR8A6FkDTTRUQg+YCPojqCPiMKT9Lsc1e2BvLdqm0oREP363Fw33C8TICdazLPHAu0+Gxzgj0Mtywq6trqoJ4o2250w+VUcvKxUKOAvX/iYHJ7NmeOhVwisIvlWZPQHFujsIDvrflmqVuEhDcp7Fna9IR8DkjlqyQNw5Am4+szQc6pyDgMZvo6S7zwBT2fI06DRsZqVTFeJEIEctwLIYlKCbsQ9BmE+wCAnamCweRDCZgvY4H1oQbUtnH1FEYOixiXbhCp+El5oG5Bqcxa5kHntPpwl7WsMjgRLqY6bBPxwMXM9f9Bos3sPvGwTpYKMvSvUAFE/GO25Lcfi5txRJn5n03nbjvWDT8zj0Rx0KcXx3BcyA9GnEjjhbXMO8OnB/j1I2TcJFW08r5zFpV3sT+5c7DbNUZU6ycuPdlVr6CXrUoNkTqHIiCEbdScF3uEQgEAoFAIBAIBIKoKW9vgRXg82Ecz8Ku4XNlBL+6VRIH2BmLUwEsj7j2mXCY/J5Ik8oGFhPqwX1Cb71uvcGlrHA9cJ1TzxtvrRnmBVUvios7cMxHAXINVS2o1B3BRdQsiwNxybe4eXlBFRagBfVOvO+elPR/8b/bm/lQUyeV4sHfxyaVsp1Oa3o95k5rHmA6FOoIeEUm1n+fWIdHXtRx3yrmuvNMFdBavxj2wNs6HviEeeBsRdGX4PunyVTjhTXSF8Gu0sJ2BO/BLuvC6UzAOh0PbGKum6Qwag2yzSF/xbmBCvc5b8u5lnrcu7ytQUfAV2yB5WSFWJH5lwWWaQr3abMJWOZx6QJLWvasI+ALRmqGysYcu4CKS3yrbO3yXeaBMx0JSOuBWcMUjUXm72AtDhaZf6aRH6KPcFkMTHW0yJy2fMo2h+7QlMV6YrWOgDeYgDkGJ9K5TjfanJStXt067GECHtVpuF02G3br0OJoDwu9SPPVmXDlEgO7b4mtoBKnq36eLcWopaAazZUZ4mYNHLW2/HSj0wtW9FCpbYM14/ODCN7yT8/ebE/obXarN4XFoda7atDAcn4Atp806It8iP4j5gi9DxogXIO14yrBI3+845I/3hEIBAKBQCAQCAQCgZvwEzFfq/dwSoGhAAAAAElFTkSuQmCC";

    /// <summary>ServerStack01 80x80 (40 px @2x), stroke #202020. PNG 1201 B, sha256 e5bc8e2c5ee2f526a4dcdc1c18fa65bfb75cb3af2286a66336a37c3696079eae.</summary>
    public const string EmptyIconLight = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAFAAAABQCAYAAACOEfKtAAAEeElEQVR42u1cfWhNYRg/CkmhrrHb7t3uNhTlY4TMmI35+Ec+Sla+P6L4c3/5CJPEkJFQRBQK8ZfP+RibbYWZ+SghKUXCbIx/FL9nnatnx5r3fXfp3Ps+v3q6Z3fnOef8fud5P87zPuc6jkAgEAgEAoFAIBAIbEBmZmavjIyMSDgcHpqamjo+PT09Lx6Nrp04pKSkpBGnfyZYcnJy30gkshMnrYb9TGD7Dp5VxBUBkhwT8XCwpThwQ4IL94eB9yd8LuqoeIfaOUEz7CXsDv4uj0eja3c5NLcj5gEj8dLS0uZ4DnQL301CnzEgKSmpR6L168SJuIFjAbje9gTLLO0+jzdbHGCHbQMlcWYiNgSDwT46zquY8zNbZxvUvKM6IDJXmPZ9aywWcB1rhft1HC8y5adbLOBsFkjnlR35fA8CjrNYwIl8ENURsC7qiNn6SEWfjTjhC9fWKo70Bdi3EvYB/qcxCgZ8JuBY1oTv6jg+ZgIO/tv+2CfFM+z/UBm13PnYT6OO+v8IOIJd3wMjAbE9SCGSctqYgI5ROM8bj/C7/CQgWsQQdn31Ok34ISM1QsGlE/Z9z072lr5TEHCfJwJ91d+iZY1iOtTqRGAtc8xW8aGMBvYvhW0PhUJhxRvVDbYE5ziBi8312yDiZpqiN7hGJwJvMcc8W0dhtIjJLJDKdAQ8xQSca7GA85kOx3QctzLlt9gqIHVHTMBNOm1/CnNshAVtE48SquDdxAIpX3cOVMZEvOq3Se4/nr4EiDPjf0n7IBhVUyHiF08kFsMK43UNRMEKXY6NjHcTBA2Z9gFZsNe2pfOZvVZ5EmsXgUCgJ8/OWLQmcpG4x3JEmkmPWm7fUJ6gdtXlOMMRCAQCgUAgEAgEAich0tt4zFkOW49Hnd2wo9g+B7sex+Vt110OR4gTcYMtI66xzI31p0VlCzMxNcjE9Oto1C20OJUVXWpdYCpeJtUM84QqrZUgGouwvRKf8yhrQanueE2gutc+w+VCnIrc9aBGltb6RgX1jkFZww1J6f8W8Zrxeqiti0rEGcJ9ZU15ktGyJm079i5rbmOBVKzTfM/IwnrrhXVoclJH+ZtMwAkWC1jAdLiiE4H3oo7oUEcrij4VfofJVPsLGump8gF2lgrb8VVXnwk4jglYrROB9cwxS8Gli2cN+TO+66xwo4575l3THP+Wt93XicAnjNRAhRPltjEBzVG4Ue88fqU+LrCs0xHwUdQRk8hhjtqLOa0EVCzxvenxW+n4q8R3uJGAVA/MaqRHqRaZw17hpM8Nisw/0siPwszePhMw27TIvEpec2jRIZ9FYLmO4wWm/GyLJ9JzTV+0OSiverV0MRtYIO3VicDV8rJhiw7Pjd5hoQdpT3VmiYXilfCEivZPAFBq21PuVel2qgmbmXEzMPnE1TO9Wmx6F8rayda+w/+fwirieE2kgji0MaHndtn4jqDMtzs9q3oy07YUWH6DbSYNnBhUrEdw0D30PGiBcMSxVPVtK0d+eOc//PCOQCAQCAQCgUAgEAj8hF/bDgkmUmecRAAAAABJRU5ErkJggg==";

    /// <summary>Every allowed image URI — the URL scan test compares against exactly this set.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        BarCpuDark,
        BarRamDark,
        BarDiskDark,
        BarStaleDark,
        BarTrackDark,
        BarCpuLight,
        BarRamLight,
        BarDiskLight,
        BarStaleLight,
        BarTrackLight,
        EmptyIconDark,
        EmptyIconLight
    ];
}
