using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class BrandingAssetTests
{
    private static readonly int[] ExpectedIconSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    [Theory]
    [InlineData("manager")]
    [InlineData("cheat")]
    public void TransparentLogoAssets_HaveExpectedDimensionsAndTransparentCorners(string kind)
    {
        string branding = Path.Combine(RepositoryRoot(), "assets", "branding");
        AssertTransparentPng(Path.Combine(branding, $"{kind}-logo-1024.png"), 1024);
        AssertTransparentPng(Path.Combine(branding, $"{kind}-logo-256.png"), 256);
    }

    [Theory]
    [InlineData("manager")]
    [InlineData("cheat")]
    public void IconFiles_ContainEveryRequiredResolution(string kind)
    {
        string iconPath = Path.Combine(RepositoryRoot(), "assets", "branding", $"{kind}.ico");
        using FileStream stream = File.OpenRead(iconPath);
        using BinaryReader reader = new(stream);

        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        int count = reader.ReadUInt16();
        Assert.Equal(ExpectedIconSizes.Length, count);

        List<int> sizes = new(count);
        for (int index = 0; index < count; index++)
        {
            int width = reader.ReadByte();
            int height = reader.ReadByte();
            reader.ReadByte();
            reader.ReadByte();
            reader.ReadUInt16();
            reader.ReadUInt16();
            uint byteCount = reader.ReadUInt32();
            uint offset = reader.ReadUInt32();

            width = width == 0 ? 256 : width;
            height = height == 0 ? 256 : height;
            Assert.Equal(width, height);
            Assert.True(byteCount > 0);
            Assert.InRange(offset, 6u + (uint)(16 * count), (uint)stream.Length - 1u);
            sizes.Add(width);
        }

        Assert.Equal(ExpectedIconSizes, sizes.OrderBy(size => size));
    }

    [Fact]
    public void ManagerAndCheatMarks_AreDistinct()
    {
        string branding = Path.Combine(RepositoryRoot(), "assets", "branding");
        byte[] managerHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(branding, "manager-logo-256.png")));
        byte[] cheatHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(branding, "cheat-logo-256.png")));

        Assert.False(managerHash.SequenceEqual(cheatHash));
    }

    [Fact]
    public void ExecutableProjects_DeclareTheExpectedApplicationIcons()
    {
        string root = RepositoryRoot();
        string launcher = File.ReadAllText(Path.Combine(root, "src", "Loopstructor.AutoPlayer.Launcher", "Loopstructor.AutoPlayer.Launcher.csproj"));
        string desktop = File.ReadAllText(Path.Combine(root, "desktop", "package.json"));
        string updater = File.ReadAllText(Path.Combine(root, "src", "Loopstructor.AutoPlayer.Updater", "Loopstructor.AutoPlayer.Updater.csproj"));

        Assert.Contains("assets\\branding\\manager.ico", launcher, StringComparison.Ordinal);
        Assert.Contains("assets/branding/manager.ico", desktop, StringComparison.Ordinal);
        Assert.Contains("assets\\branding\\manager.ico", updater, StringComparison.Ordinal);
        Assert.Contains("Loopstructor-2-QA-Tool", desktop, StringComparison.Ordinal);
    }

    private static void AssertTransparentPng(string path, int expectedSize)
    {
        byte[] png = File.ReadAllBytes(path);
        byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        Assert.True(png.AsSpan(0, signature.Length).SequenceEqual(signature));

        int width = 0;
        int height = 0;
        int bitDepth = 0;
        int colorType = 0;
        int interlaceMethod = 0;
        List<byte> compressed = new();
        for (int offset = signature.Length; offset + 12 <= png.Length;)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
            string type = Encoding.ASCII.GetString(png, offset + 4, 4);
            ReadOnlySpan<byte> data = png.AsSpan(offset + 8, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
                height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(4, 4));
                bitDepth = data[8];
                colorType = data[9];
                interlaceMethod = data[12];
            }
            else if (type == "IDAT")
            {
                compressed.AddRange(data.ToArray());
            }

            offset += 12 + length;
            if (type == "IEND") break;
        }

        Assert.Equal(expectedSize, width);
        Assert.Equal(expectedSize, height);
        Assert.Equal(8, bitDepth);
        Assert.Equal(6, colorType);
        Assert.Equal(0, interlaceMethod);
        Assert.NotEmpty(compressed);

        int stride = checked(width * 4);
        byte[] filtered = new byte[checked((stride + 1) * height)];
        using (ZLibStream decoder = new(new MemoryStream(compressed.ToArray()), CompressionMode.Decompress))
        {
            int read = 0;
            while (read < filtered.Length)
            {
                int count = decoder.Read(filtered, read, filtered.Length - read);
                if (count == 0) break;
                read += count;
            }

            Assert.Equal(filtered.Length, read);
        }

        byte[] previous = new byte[stride];
        byte[] current = new byte[stride];
        byte[] cornerAlpha = new byte[4];
        bool hasOpaquePixel = false;
        for (int y = 0; y < height; y++)
        {
            int sourceOffset = y * (stride + 1);
            byte filter = filtered[sourceOffset++];
            for (int x = 0; x < stride; x++)
            {
                int left = x >= 4 ? current[x - 4] : 0;
                int up = previous[x];
                int upLeft = x >= 4 ? previous[x - 4] : 0;
                int value = filtered[sourceOffset + x];
                current[x] = (byte)((value + (filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => throw new InvalidDataException($"PNG 使用了未知过滤器 {filter}。")
                })) & 0xFF);
            }

            for (int x = 0; x < width; x++)
            {
                byte alpha = current[x * 4 + 3];
                hasOpaquePixel |= alpha == byte.MaxValue;
                if ((x == 0 || x == width - 1) && (y == 0 || y == height - 1))
                {
                    int corner = y == 0 ? (x == 0 ? 0 : 1) : (x == 0 ? 2 : 3);
                    cornerAlpha[corner] = alpha;
                }
            }

            (previous, current) = (current, previous);
        }

        Assert.All(cornerAlpha, alpha => Assert.Equal(0, alpha));
        Assert.True(hasOpaquePixel);
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        int estimate = left + up - upLeft;
        int leftDistance = Math.Abs(estimate - left);
        int upDistance = Math.Abs(estimate - up);
        int upLeftDistance = Math.Abs(estimate - upLeft);
        return leftDistance <= upDistance && leftDistance <= upLeftDistance
            ? left
            : upDistance <= upLeftDistance ? up : upLeft;
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Loopstructor.AutoPlayer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate the repository root.");
    }
}
