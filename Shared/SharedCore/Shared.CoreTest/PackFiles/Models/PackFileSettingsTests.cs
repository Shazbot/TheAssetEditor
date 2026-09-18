using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Moq;
using Shared.Core.PackFiles.Models;
using Shared.Core.Services;
using Shared.Core.Settings;

namespace Shared.CoreTest.PackFiles.Models
{
    [TestFixture]
    internal class PackFileSettingsTests
    {
        [Test]
        public void Save_WhenSerializeToDiskFalse_DoesNotWriteSettingsFile()
        {
            var fileSystem = new Mock<IFileSystemAccess>();
            var settings = new PackFileSettings
            {
                SerializeToDisk = false,
                SaveLocationPath = @"c:\output\test.pack"
            };

            settings.Save(@"c:\project\aeproject.json", fileSystem.Object);

            fileSystem.Verify(x => x.FileWriteAllBytes(It.IsAny<string>(), It.IsAny<byte[]>()), Times.Never);
        }

        [Test]
        public void Save_WhenSerializeToDiskTrue_WritesPackFileSettingsJson()
        {
            var fileSystem = new Mock<IFileSystemAccess>();
            byte[]? savedBytes = null;
            fileSystem.Setup(x => x.FileWriteAllBytes(@"c:\project\aeproject.json", It.IsAny<byte[]>()))
                .Callback<string, byte[]>((_, bytes) => savedBytes = bytes);
            var settings = new PackFileSettings
            {
                SerializeToDisk = true,
                SaveLocationPath = @"c:\output\test.pack",
                GameVersion = GameTypeEnum.Warhammer3,
                EnablePackFileCorruptionDetection = true,
                IgnoredFilesWhenSerializing = new ObservableCollection<string>([@"Folder/IGNORE.txt", @"folder\ignore.txt", ""])
            };

            settings.Save(@"c:\project\aeproject.json", fileSystem.Object);

            Assert.That(savedBytes, Is.Not.Null);
            var json = Encoding.UTF8.GetString(savedBytes!);
            Assert.That(json, Does.Contain("SaveLocationPath"));
            Assert.That(json, Does.Contain("EnablePackFileCorruptionDetection"));
            Assert.That(json, Does.Contain("Warhammer3"));
            Assert.That(json, Does.Contain(@"folder\\ignore.txt"));
            Assert.That(json, Does.Not.Contain("SerializeToDisk"));
        }

        [Test]
        public void Load_ReadsSerializedPackFileSettings()
        {
            var fileSystem = new Mock<IFileSystemAccess>();
            var json = """
            {
              "SaveLocationPath": "c:\\output\\test.pack",
              "GameVersion": "Warhammer3",
              "EnablePackFileCorruptionDetection": true,
              "IgnoredFilesWhenSerializing": [ "db\\ignored.tsv" ]
            }
            """;
            fileSystem.Setup(x => x.FileReadAllBytes(@"c:\project\aeproject.json"))
                .Returns(Encoding.UTF8.GetBytes(json));

            var loaded = PackFileSettings.Load(@"c:\project\aeproject.json", fileSystem.Object);

            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.SaveLocationPath, Is.EqualTo(@"c:\output\test.pack"));
            Assert.That(loaded.GameVersion, Is.EqualTo(GameTypeEnum.Warhammer3));
            Assert.That(loaded.EnablePackFileCorruptionDetection, Is.True);
            Assert.That(loaded.IgnoredFilesWhenSerializing, Does.Contain(@"db\ignored.tsv"));
            Assert.That(loaded.SerializeToDisk, Is.False);
        }

        [Test]
        public void SaveAndLoad_RoundTripsAllSerializedSettings()
        {
            var fileSystem = new Mock<IFileSystemAccess>();
            byte[]? savedBytes = null;
            fileSystem.Setup(x => x.FileWriteAllBytes("settings.json", It.IsAny<byte[]>()))
                .Callback<string, byte[]>((_, bytes) => savedBytes = bytes);

            var settings = new PackFileSettings
            {
                SerializeToDisk = true,
                SaveLocationPath = @"c:\output\test.pack",
                GameVersion = GameTypeEnum.Warhammer3,
                EnablePackFileCorruptionDetection = true,
                IgnoredFilesWhenSerializing = new ObservableCollection<string>(["Folder/IGNORE.txt", ""])
            };

            settings.Save("settings.json", fileSystem.Object);
            fileSystem.Setup(x => x.FileReadAllBytes("settings.json")).Returns(savedBytes!);

            var loaded = PackFileSettings.Load("settings.json", fileSystem.Object);

            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.SaveLocationPath, Is.EqualTo(settings.SaveLocationPath));
            Assert.That(loaded.GameVersion, Is.EqualTo(settings.GameVersion));
            Assert.That(loaded.EnablePackFileCorruptionDetection, Is.EqualTo(settings.EnablePackFileCorruptionDetection));
            Assert.That(loaded.IgnoredFilesWhenSerializing, Is.EqualTo(new[] { @"folder\ignore.txt" }));
        }

        [Test]
        public void Load_ReadsLegacyOutputPackFilePath()
        {
            var fileSystem = new Mock<IFileSystemAccess>();
            var json = """
            {
              "OutputPackFilePath": "c:\\output\\legacy.pack",
              "EnablePackFileCorruptionDetection": true
            }
            """;
            fileSystem.Setup(x => x.FileReadAllBytes(@"c:\project\project_ignore.json"))
                .Returns(Encoding.UTF8.GetBytes(json));

            var loaded = PackFileSettings.Load(@"c:\project\project_ignore.json", fileSystem.Object);

            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.SaveLocationPath, Is.EqualTo(@"c:\output\legacy.pack"));
        }

        [Test]
        public void ApplySerializedSettings_NormalizesIgnoredFiles()
        {
            var target = new PackFileSettings();
            var source = new PackFileSettings
            {
                IgnoredFilesWhenSerializing = new ObservableCollection<string>([@"Folder/IGNORE.txt", @"folder\ignore.txt", ""])
            };

            target.ApplySerializedSettings(source);

            Assert.That(target.IgnoredFilesWhenSerializing, Is.EqualTo(new[] { @"folder\ignore.txt" }));
        }
    }
}
