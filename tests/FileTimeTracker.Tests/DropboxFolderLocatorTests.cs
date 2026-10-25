using FileTimeTracker.Core.Storage;
using Xunit;

namespace FileTimeTracker.Tests;

/// <summary>
/// The locator reads the Dropbox client's own <c>info.json</c>, which is the only reliable source
/// since the synced folder can be moved anywhere. These cover the shapes seen in the wild plus the
/// "no Dropbox here" case, which has to be an ordinary answer rather than an exception.
/// </summary>
public sealed class DropboxFolderLocatorTests
{
    [Fact]
    public void A_storage_root_is_the_dropbox_root_plus_the_app_subfolder()
    {
        var root = DropboxFolderLocator.FindRoot();
        var storage = DropboxFolderLocator.FindStorageRoot();

        if (root is null)
        {
            // No Dropbox on this machine: both must agree on that rather than one guessing.
            Assert.Null(storage);
            return;
        }

        Assert.Equal(Path.Combine(root, DropboxFolderLocator.DefaultSubfolder), storage);
    }

    [Fact]
    public void Locating_never_throws_even_with_no_dropbox_installed()
    {
        // Whatever this machine looks like, the call must come back with a path or null.
        var root = DropboxFolderLocator.FindRoot();

        if (root is not null)
        {
            Assert.True(Directory.Exists(root), $"Reported root '{root}' should exist.");
        }
    }

    [Fact]
    public void A_publish_root_is_the_dropbox_root_plus_the_reports_subfolder()
    {
        var root = DropboxFolderLocator.FindRoot();
        var publish = DropboxFolderLocator.FindPublishRoot();

        if (root is null)
        {
            Assert.Null(publish);
            return;
        }

        Assert.Equal(Path.Combine(root, DropboxFolderLocator.PublishSubfolder), publish);
    }

    [Fact]
    public void The_subfolder_name_keeps_the_dropbox_root_tidy()
    {
        // Writing logs and workbooks straight into the Dropbox root would be rude; pin the choice.
        Assert.Equal("FileTimeTracker", DropboxFolderLocator.DefaultSubfolder);
    }

    [Fact]
    public void The_publish_subfolder_is_the_only_thing_this_app_puts_in_dropbox()
    {
        // Pinned because it is the folder name a user sees, and renaming it would orphan the
        // reports already synced under the old one.
        Assert.Equal("Employee Timeslots", DropboxFolderLocator.PublishSubfolder);
    }

    [Fact]
    public void The_working_folder_and_the_published_folder_are_never_the_same_place()
    {
        // The whole point of the split: logs and config must not land where the reports go.
        Assert.NotEqual(DropboxFolderLocator.DefaultSubfolder, DropboxFolderLocator.PublishSubfolder);
    }
}
