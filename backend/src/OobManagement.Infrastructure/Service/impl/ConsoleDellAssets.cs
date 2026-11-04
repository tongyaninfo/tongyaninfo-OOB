namespace OobManagement.Infrastructure.Service.impl;

/// <summary>已核实 iDRAC8 2.85.85.85 与 2.86.86.86 的同一组原生脚本指纹；正文运行时从设备获取，不重分发厂商代码。</summary>
internal static class ConsoleDellAssets
{
    public static readonly IReadOnlyDictionary<string, string> Hashes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["rpviewer.js"] = "9d193cf48e6cbf5139beab101d6ddb4310119a84699877f2c824b0d3187bb829",
        ["rphandlers.js"] = "ca44953a4b51779e7dcb3c917cd52e5ae7b2856002788c73b08183d84ebec007",
        ["websockethandler.js"] = "69d0eadfe983cc7b42cad548442292dc8336603c933523ab7b1ddbd9dceda7e9",
        ["virtualkeyboard.js"] = "77ab97ae25462690671d5674821c5b864c64eb9703706a23a6b95a28ef1e33be",
        ["utility.js"] = "f54486e8c425b4371412b3500ea46387604a0c26207975083177352012d76f0a",
        ["mediaTypes.js"] = "81a620ec21983f3b7027c3d3ca519720f5736fdf00bbf8a7e9d0b6710bfe5275",
        ["mediaworkerhandler.js"] = "10b652fe59c25b00e91bd3537e89470a072a80097ed7a100911647d95f996d46",
        ["dvcmainworker.js"] = "bd0fbe5ac5436bba089039f4f7731df9de6f59caa42726cd9288a23d7fba7c49",
        ["decoderworker.js"] = "72c9fa4397a23112e40f245445b5e496bd618ea0384ebe55fe4b4750dbcee45d",
        ["offscreenworker.js"] = "05b6f9ce1d4e5452f762aa507f3c2f143dbef24ac83ed73e99130cbf000fceaa",
        ["mouseworker.js"] = "e2de425aff125f54868d4bc920c3f4d5a6799a42ff3c23af0d523bc481839684",
        ["mediaworker.js"] = "7fe36b75c4acc7959ab75fe54385e1c8ab55aef4ab629d8f59ecbc0b18b6aff4",
        ["dvcdecoder.js"] = "0609905ca22f6c8d3411ef3adb66dff8e29b2df624afab866d3e1f745255e960",
        ["dvcsubworker.js"] = "ec66f9828399f63991143295dc9f7c425c8599689e1085f742536ba0c293f4c7",
        ["decompressdvc.js"] = "ae721bebdfbe78ea5b7f125d501c183ebb27947ccd244acd1603ffad62efd89c",
        ["vmWebSocketHandler.js"] = "89e2ce368e5f58d62781d22735fb200ff2eab35654489624460473b065e898d6",
        ["fileworker.js"] = "c5ea52df93c8e94e50f0ddeecb6eaa9dd57c63157988c8278a8ea4b3b140c1c4",
        ["vmResponseHandler.js"] = "4906f69746de29a1ad5fd9b67f7001cbd529c95c66989c230a5741858a5377b5",
        ["bootsector.js"] = "7c5ef044f2cbf30f030e445aed1ca8b39e8e959452ad43bba3f7d87dc3e2c5e3",
    };
}
