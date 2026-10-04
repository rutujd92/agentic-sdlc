using System.Security.Cryptography;
using System.Text;

namespace Orchestrator.Core.Execution;

/// <summary>Output of a node. The content hash links every downstream decision to the exact inputs it used.</summary>
public sealed record Artifact(string NodeId, string Content, string Hash)
{
    public static Artifact Create(string nodeId, string content) =>
        new(nodeId, content, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))));
}
