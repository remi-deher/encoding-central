namespace Encoding.NodeAgent.Runtime.Media;

/// <summary>Publication demandée par un bloc de flow dans un état qui ne la permet pas (ADR 0003).</summary>
public sealed class PublishResultException(string message) : Exception(message);
