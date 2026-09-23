using cl2j.FileStorage.Core;
using cl2j.FileStorage.Extensions;
using Xunit;

namespace cl2j.FileStorage.Tests
{
    /// <summary>
    ///     What every <see cref="IFileStorageProvider"/> has to do, regardless of where it puts the
    ///     bytes. A provider is tested by deriving from this and saying which one it is; the tests
    ///     come with it, so a second implementation cannot quietly satisfy fewer of them.
    ///
    ///     <para>
    ///     The shape is inherited from the tests this replaces, which had the same intent expressed
    ///     as a helper class each runner forwarded to by hand. Deriving means a test added here
    ///     runs for every provider without anyone remembering to forward it.
    ///     </para>
    /// </summary>
    public abstract class FileStorageProviderContract
    {
        protected abstract IFileStorageProvider Provider { get; }

        /// <summary>
        ///     A directory of its own per test, so nothing depends on what another test left
        ///     behind. The tests this replaces shared one directory and cleared it on the way in,
        ///     which only works while they run one at a time.
        /// </summary>
        private static string Folder(string test) => $"contract-{test}";

        private static string File(string test, string name = "file.txt") => $"{Folder(test)}/{name}";

        [Fact]
        public async Task What_was_written_is_what_is_read_back()
        {
            var file = File(nameof(What_was_written_is_what_is_read_back));

            await Provider.WriteTextAsync(file, "hello");

            Assert.True(await Provider.ExistsAsync(file));
            Assert.Equal("hello", await Provider.ReadTextAsync(file));
        }

        [Fact]
        public async Task Writing_over_a_file_replaces_it_rather_than_adding_to_it()
        {
            var file = File(nameof(Writing_over_a_file_replaces_it_rather_than_adding_to_it));

            await Provider.WriteTextAsync(file, "the first, which is longer");
            await Provider.WriteTextAsync(file, "second");

            Assert.Equal("second", await Provider.ReadTextAsync(file));
        }

        [Fact]
        public async Task Appending_adds_to_what_is_already_there()
        {
            var file = File(nameof(Appending_adds_to_what_is_already_there));

            await Provider.AppendTextAsync(file, "first");
            await Provider.AppendTextAsync(file, "second");

            Assert.Equal("firstsecond", await Provider.ReadTextAsync(file));
        }

        [Fact]
        public async Task Appending_to_a_file_that_is_not_there_creates_it()
        {
            var file = File(nameof(Appending_to_a_file_that_is_not_there_creates_it));

            await Provider.AppendTextAsync(file, "from nothing");

            Assert.Equal("from nothing", await Provider.ReadTextAsync(file));
        }

        [Fact]
        public async Task A_listing_names_the_files_that_are_there()
        {
            var folder = Folder(nameof(A_listing_names_the_files_that_are_there));

            await Provider.WriteTextAsync($"{folder}/one.txt", "1");
            await Provider.WriteTextAsync($"{folder}/two.txt", "2");

            var files = await Provider.ListFilesAsync(folder);

            //The count alone let a real defect through: the disk provider cut the root off each
            //path by length, and every name came back missing its first character. Counting two
            //files said nothing about ".txt" standing where "one.txt" was expected.
            Assert.Equal(["one.txt", "two.txt"], files.OrderBy(n => n, StringComparer.Ordinal));
        }

        [Fact]
        public async Task A_listing_names_the_files_the_same_way_when_the_folder_ends_with_a_separator()
        {
            var folder = Folder(nameof(A_listing_names_the_files_the_same_way_when_the_folder_ends_with_a_separator));

            await Provider.WriteTextAsync($"{folder}/one.txt", "1");

            //A caller is free to hand in "folder/". The disk provider used to build a root ending
            //with a separator from it, which threw its name cutting off by one.
            Assert.Equal(["one.txt"], await Provider.ListFilesAsync($"{folder}/"));
        }

        [Fact]
        public async Task A_listing_names_the_folders_that_are_there()
        {
            var folder = Folder(nameof(A_listing_names_the_folders_that_are_there));

            await Provider.WriteTextAsync($"{folder}/first/file.txt", "1");
            await Provider.WriteTextAsync($"{folder}/second/file.txt", "2");

            var folders = await Provider.ListFoldersAsync(folder);

            Assert.Equal(["first", "second"], folders.OrderBy(n => n, StringComparer.Ordinal));
        }

        [Fact]
        public async Task A_listing_of_a_folder_with_nothing_in_it_is_empty_rather_than_an_error()
        {
            var folder = Folder(nameof(A_listing_of_a_folder_with_nothing_in_it_is_empty_rather_than_an_error));

            //Emptied rather than never written: every provider agrees the folder was there, so the
            //only thing under test is what "nothing left" answers. Version 4 of the AWS SDK leaves
            //the response collections null instead of empty, which turned this into a
            //NullReferenceException against a real bucket — housekeeping that deletes and then
            //lists is exactly where it landed.
            await Provider.WriteTextAsync($"{folder}/only.txt", "1");
            await Provider.DeleteAsync($"{folder}/only.txt");

            Assert.Empty(await Provider.ListFilesAsync(folder));
            Assert.Empty(await Provider.ListFoldersAsync(folder));
        }

        [Fact]
        public async Task A_deleted_file_is_gone()
        {
            var file = File(nameof(A_deleted_file_is_gone));
            await Provider.WriteTextAsync(file, "doomed");

            await Provider.DeleteAsync(file);

            Assert.False(await Provider.ExistsAsync(file));
        }

        [Fact]
        public async Task A_file_that_was_never_written_does_not_exist()
        {
            Assert.False(await Provider.ExistsAsync(File(nameof(A_file_that_was_never_written_does_not_exist), "never-written.txt")));
        }

        [Fact]
        public async Task Writing_into_a_folder_that_is_not_there_creates_it()
        {
            var file = $"{Folder(nameof(Writing_into_a_folder_that_is_not_there_creates_it))}/deeper/still/file.txt";

            await Provider.WriteTextAsync(file, "nested");

            Assert.Equal("nested", await Provider.ReadTextAsync(file));
        }
    }
}
