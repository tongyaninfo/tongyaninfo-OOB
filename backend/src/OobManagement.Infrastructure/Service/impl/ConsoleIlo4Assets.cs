namespace OobManagement.Infrastructure.Service.impl;

/// <summary>iLO4 2.82 已审查原样脚本的 SHA-256；只保存摘要，厂商脚本在会话内按白名单获取。</summary>
internal static class ConsoleIlo4Assets
{
    public static readonly IReadOnlyDictionary<string, string> ReviewedHashes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["jquery.js"] = "16c2f853b154ba00aa7de935dc36b8f66c69516595f61633ed48643369eaca67",
        ["constants.js"] = "9fa45540d3cad2ae92533bb5c515210b7e581d7b7d8fb1eaa67dda5732d2e8da",
        ["cache.js"] = "a9eea0f2678f498a226517195a835c449692a6a6a78ef5b8187f99c4fc524f5c",
        ["state.js"] = "9fa5d13b4ddb16defc99f6d59e06d10aaa2331dff29d2ec9f53857d12fcc594a",
        ["socket.js"] = "eaf51490c30deb91614b897a5c612b5fb154b175e09e0e343fa04bd60ba15367",
        ["pseudo-worker.js"] = "642c30b92ab54d3b59bd2de36e8afd2ba2fab78afdaf646a0fca9c640abbdc5e",
        ["ircKeyboardMouse.js"] = "a5e9c17f150d0485e8c9ef055e846a381f1e761ff82bd9658bc34dd5c2f0947e",
        ["renderer.js"] = "2d9748c1819dbf66594e1cdd9a8e8b109b6e17654f388f817800c27776b08819",
        ["worker_decoder.js"] = "29fe78c83fb77372a0fc69d3b45d95f426fdb7e2859b4aef05cdfdf42b410602",
        ["VMconnection.js"] = "54c4e7e246d9c6083ed447c7bd1b6f9092da0c55f5f0532c2beaaebd36988be8"
    };
}
