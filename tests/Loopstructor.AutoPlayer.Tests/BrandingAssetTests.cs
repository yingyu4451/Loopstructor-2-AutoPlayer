using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
        Assert.Contains("Loopstructor.AutoPlayer.Manager", desktop, StringComparison.Ordinal);
        Assert.Contains("Assets/Branding/manager-logo-256.png", updater, StringComparison.Ordinal);
    }

    private static void AssertTransparentPng(string path, int expectedSize)
    {
        using FileStream stream = File.OpenRead(path);
        BitmapFrame frame = PngBitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];

        Assert.Equal(expectedSize, frame.PixelWidth);
        Assert.Equal(expectedSize, frame.PixelHeight);

        FormatConvertedBitmap converted = new(frame, PixelFormats.Bgra32, null, 0d);
        int stride = converted.PixelWidth * 4;
        byte[] pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        int[] cornerAlphaOffsets =
        {
            3,
            (converted.PixelWidth - 1) * 4 + 3,
            (converted.PixelHeight - 1) * stride + 3,
            pixels.Length - 1
        };
        Assert.All(cornerAlphaOffsets, offset => Assert.Equal(0, pixels[offset]));
        Assert.Contains(Enumerable.Range(0, converted.PixelWidth * converted.PixelHeight),
            index => pixels[index * 4 + 3] == byte.MaxValue);
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
