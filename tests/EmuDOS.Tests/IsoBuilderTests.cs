using System.Text;
using EmuDOS.Core.Import;

namespace EmuDOS.Tests;

public class IsoBuilderTests
{
    [Fact]
    public void BuildFromFolder_produces_a_readable_iso9660_image()
    {
        var src = Path.Combine(Path.GetTempPath(), "emudos_iso_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "READOUT.TXT"), "hello from EmuDOS");
        var iso = src + ".iso";

        try
        {
            if (!IsoBuilder.IsAvailable)
            {
                // No xorriso/genisoimage/mkisofs on PATH — the builder must fail clearly, not silently.
                Assert.Throws<NotSupportedException>(() => IsoBuilder.BuildFromFolder(src, iso, "TEST DISC"));
                return;
            }

            IsoBuilder.BuildFromFolder(src, iso, "TEST DISC");

            Assert.True(File.Exists(iso));
            Assert.True(new FileInfo(iso).Length >= 16 * 2048);

            // ISO9660 primary volume descriptor: the magic "CD001" sits at sector 16, offset 1.
            using var fs = File.OpenRead(iso);
            fs.Seek(16 * 2048 + 1, SeekOrigin.Begin);
            var magic = new byte[5];
            fs.ReadExactly(magic);
            Assert.Equal("CD001", Encoding.ASCII.GetString(magic));
        }
        finally
        {
            try { Directory.Delete(src, recursive: true); } catch { /* best effort */ }
            try { File.Delete(iso); } catch { /* best effort */ }
        }
    }
}
