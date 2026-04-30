namespace Infrastructure.DataLocalization;

public interface IConnectionResolver
{
    /// <summary>
    /// Trả về Connection String dựa trên Brand (Horizontal Fragmentation).
    /// </summary>
    string Resolve(string brand, bool readOnly = false);

    /// <summary>
    /// Overload trực tiếp bằng NodeRoute — dùng khi caller đã biết node cụ thể.
    /// </summary>
    string Resolve(NodeRoute route);
}