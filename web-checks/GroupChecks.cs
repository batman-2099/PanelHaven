using System.IO.Compression;
using System.Xml.Linq;
using PanelHaven.Web;
using Microsoft.Data.Sqlite;

public static class GroupChecks
{
    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static void Reject<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new Exception($"Expected {typeof(T).Name}.");
        }
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-groups-" + Guid.NewGuid().ToString("N"));
        var firstRoot = Path.Combine(directory, "first");
        var secondRoot = Path.Combine(directory, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        try
        {
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            var fixtures = new[]
            {
                (Title: "Alpha", Series: "One", Publisher: "Press", Folder: Path.Combine(firstRoot, "Stacks")),
                (Title: "Beta", Series: "One", Publisher: "Press", Folder: Path.Combine(firstRoot, "Stacks")),
                (Title: "Gamma", Series: "Two", Publisher: "Other", Folder: Path.Combine(firstRoot, "Other")),
                (Title: "Delta", Series: "Two", Publisher: "Press", Folder: Path.Combine(firstRoot, "Other")),
                (Title: "Empty", Series: "", Publisher: "", Folder: firstRoot),
                (Title: "Lower", Series: "one", Publisher: "Press", Folder: Path.Combine(secondRoot, "Stacks"))
            };
            foreach (var fixture in fixtures)
            {
                Directory.CreateDirectory(fixture.Folder);
                using var archive = ZipFile.Open(Path.Combine(fixture.Folder, fixture.Title + ".cbz"), ZipArchiveMode.Create);
                using (var stream = archive.CreateEntry("1.png").Open()) stream.Write(png);
                using var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open());
                writer.Write(new XElement("ComicInfo", new XElement("Title", fixture.Title),
                    new XElement("Series", fixture.Series), new XElement("Publisher", fixture.Publisher),
                    new XElement("Summary", "marker"), new XElement("Number", fixture.Title switch
                    {
                        "Alpha" => "2", "Beta" => "10", "Gamma" => "3", "Delta" => "20", "Empty" => "1", _ => "11"
                    })));
            }
            var store = new LibraryStore(Path.Combine(directory, "groups.sqlite"), [firstRoot, secondRoot]);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == fixtures.Length, "Group fixtures failed to import.");
            var all = store.ListBooks("alice", null).Items;
            var ids = all.ToDictionary(book => book.Title, book => book.Id);
            var groups = store.ListGroups("alice", null, "series");
            Check(groups.Total == 4 && groups.Items.Sum(group => group.Count) == 6
                && groups.Items.Single(group => group.Key == "One").Count == 2
                && groups.Items.Single(group => group.Key == "one").Count == 1
                && groups.Items.Single(group => group.Key == "").Count == 1,
                "Series groups lost exact keys, counts, or the empty group.");
            Check(groups.Items.Single(group => group.Key == "One").CoverId == ids["Alpha"]
                && store.ListGroups("alice", null, "series", sort: "number", direction: "desc")
                    .Items.Single(group => group.Key == "One").CoverId == ids["Alpha"],
                "Series covers must use natural issue order, independently of stack arrangement.");
            store.SaveProgress("cover-reader", ids["Alpha"], new(0, true));
            Check(store.ListGroups("cover-reader", null, "series").Items.Single(group => group.Key == "One").CoverId == ids["Beta"]
                && store.ListGroups("bob", null, "series").Items.Single(group => group.Key == "One").CoverId == ids["Alpha"]
                && store.ListGroups("cover-reader", "Alpha", "series").Items.Single().CoverId == ids["Alpha"],
                "Series covers ignored private completion or escaped the current filter.");
            store.SaveProgress("cover-reader", ids["Beta"], new(0, false));
            Check(store.ListGroups("cover-reader", null, "series", sectionBy: "publisher")
                    .Items.Single(group => group.Key == "One").CoverId == ids["Beta"],
                "An in-progress issue must remain the first unfinished series cover.");
            store.SaveProgress("cover-reader", ids["Beta"], new(0, true));
            Check(store.ListGroups("cover-reader", null, "series").Items.Single(group => group.Key == "One").CoverId == ids["Alpha"],
                "A completed series must fall back to its first available issue.");
            store.SaveProgress("cover-reader", ids["Alpha"], new(0, false));
            Check(store.ListGroups("cover-reader", null, "series").Items.Single(group => group.Key == "One").CoverId == ids["Alpha"],
                "Marking an issue unfinished did not refresh the series cover.");
            var pages = Enumerable.Range(1, 2).Select(page => store.ListGroups("alice", null, "series", page, 2)).ToArray();
            Check(pages.All(page => page.Total == 4 && page.Items.Count == 2 && page.PageSize == 2)
                && pages.SelectMany(page => page.Items).SequenceEqual(groups.Items)
                && store.ListGroups("alice", null, "series", int.MaxValue, 2).Page == 2,
                "Group pagination or stable ordering failed.");
            foreach (var groupBy in new[] { "series", "publisher", "folder" })
            foreach (var group in store.ListGroups("alice", null, groupBy).Items)
            {
                var members = store.ListBooks("alice", null, groupBy: groupBy, groupKey: group.Key);
                var memberPages = Enumerable.Range(1, group.Count)
                    .Select(page => store.ListBooks("alice", null, page, 1, groupBy: groupBy, groupKey: group.Key)).ToArray();
                Check(members.Total == group.Count && members.Items.Any(book => book.Id == group.CoverId)
                    && memberPages.All(page => page.Total == group.Count && page.Items.Count == 1)
                    && memberPages.SelectMany(page => page.Items).SequenceEqual(members.Items),
                    "Stack drilldown disagreed with grouped count, cover, or pagination.");
            }
            var folders = store.ListGroups("alice", null, "folder");
            Check(folders.Total == 4 && folders.Items.All(group => !Path.IsPathRooted(group.Key)
                && !group.Key.Contains(directory)) && folders.Items.Count(group => group.Key.EndsWith("/Stacks")) == 2,
                "Folder groups leaked absolute paths or merged configured roots.");
            Check(store.ListGroups("alice", null, "publisher").Items.Single(group => group.Key == "").Count == 1
                && store.ListBooks("alice", null, groupBy: "series", groupKey: "").Items.Single().Id == ids["Empty"]
                && store.ListBooks("alice", null, groupBy: "series").Items.SequenceEqual(all),
                "Empty-key drilldown or flat browsing changed.");
            var empty = store.ListGroups("alice", "absent", "series", int.MaxValue, 1);
            Check(empty.Total == 0 && empty.Page == 1 && empty.Items.Count == 0, "Empty groups were not normalized.");

            Check(groups.Sections.Count == 0 && store.ListBooks("alice", null).Sections.Count == 0,
                "Omitted section grouping changed the old API.");
            foreach (var direction in new[] { "asc", "desc" })
            {
                var arranged = store.ListGroups("alice", null, "series", sort: "number", direction: direction);
                Check(arranged.Items.Select(group => group.Key).SequenceEqual(direction == "asc"
                    ? new[] { "", "One", "Two", "one" } : new[] { "Two", "one", "One", "" }),
                    "Stack arrangement lost natural MIN/MAX issue ordering.");
                var sectioned = store.ListGroups("alice", null, "series", sectionBy: "publisher",
                    sort: "number", direction: direction);
                Check(sectioned.Total == 5 && sectioned.Items.Count(group => group.Key == "Two") == 2
                    && sectioned.Sections.Select(section => (section.Key, section.Start, section.Count))
                        .SequenceEqual(new[] { ("", 0, 1), ("Other", 1, 1), ("Press", 2, 3) })
                    && sectioned.Sections[0].Label == "Unknown publisher",
                    "Independent sections merged stacks or changed exact section ordering.");
                var paged = Enumerable.Range(1, 3).Select(page => store.ListGroups("alice", null, "series",
                    page, 2, sectionBy: "publisher", sort: "number", direction: direction)).ToArray();
                Check(paged.SelectMany(page => page.Items).SequenceEqual(sectioned.Items)
                    && paged.All(page => page.Total == 5 && page.Sections.Sum(section => section.Count) == page.Items.Count)
                    && paged[1].Sections.Single() is { Key: "Press", Start: 0, Count: 2 }
                    && paged[2].Sections.Single() is { Key: "Press", Start: 0, Count: 1 },
                    "Section ranges were not contiguous and page-local.");
                foreach (var section in sectioned.Sections)
                foreach (var stack in sectioned.Items.Skip(section.Start).Take(section.Count))
                {
                    var members = store.ListBooks("alice", null, groupBy: "series", groupKey: stack.Key,
                        sectionBy: "publisher", sectionKey: section.Key);
                    Check(members.Total == stack.Count && members.Items.All(book => book.Publisher == section.Key
                        && book.Series == stack.Key) && members.Items.Any(book => book.Id == stack.CoverId),
                        "Dual-key stack drilldown leaked another section or selected its cover.");
                }
                var books = store.ListBooks("alice", null, sectionBy: "publisher", sort: "number", direction: direction);
                Check(books.Items.Select(book => book.Title).SequenceEqual(direction == "asc"
                    ? new[] { "Empty", "Gamma", "Alpha", "Beta", "Lower", "Delta" }
                    : new[] { "Empty", "Gamma", "Delta", "Lower", "Beta", "Alpha" }),
                    "Book arrangement overrode section ordering or sorted issue numbers lexically.");
                var bookPages = Enumerable.Range(1, 3).Select(page => store.ListBooks("alice", null,
                    page, 2, "number", direction, sectionBy: "publisher")).ToArray();
                Check(bookPages.SelectMany(page => page.Items).SequenceEqual(books.Items)
                    && bookPages.All(page => page.Sections.Sum(section => section.Count) == page.Items.Count),
                    "Sectioned book pagination skipped members or returned global ranges.");
            }
            foreach (var sectionBy in new[] { "series", "publisher", "folder" })
            foreach (var groupBy in new[] { "series", "publisher", "folder" })
            {
                var sectioned = store.ListGroups("alice", null, groupBy, sectionBy: sectionBy);
                Check(sectioned.Items.Sum(group => group.Count) == 6
                    && sectioned.Sections.All(section => !section.Key.Contains(directory)),
                    "Section/stack combinations lost members or leaked an absolute folder.");
                foreach (var section in sectioned.Sections)
                foreach (var stack in sectioned.Items.Skip(section.Start).Take(section.Count))
                    Check(store.ListBooks("alice", null, groupBy: groupBy, groupKey: stack.Key,
                        sectionBy: sectionBy, sectionKey: section.Key).Total == stack.Count,
                        "A section/stack combination could not drill down exactly.");
            }
            store.EditMetadata(ids["Lower"], new(new() { ["Publisher"] = "press" }));
            Check(store.ListGroups("alice", null, "series", sectionBy: "publisher").Sections
                .Select(section => section.Key).SequenceEqual(["", "Other", "Press", "press"])
                && store.ListBooks("alice", null, sectionBy: "publisher", sectionKey: "Press").Total == 3
                && store.ListBooks("alice", null, sectionBy: "publisher", sectionKey: "press").Total == 1,
                "Section keys merged case variants or lost their binary tie-break.");
            store.EditMetadata(ids["Lower"], new(new() { ["Publisher"] = "Press" }));
            store.EditMetadata(ids["Gamma"], new(new() { ["Title"] = "a" }));
            store.EditMetadata(ids["Delta"], new(new() { ["Title"] = "Z" }));
            Check(store.ListGroups("alice", null, "series", sort: "title").Items.Select(group => group.Key)
                    .SequenceEqual(["Two", "One", "", "one"])
                && store.ListGroups("alice", null, "series", sort: "title", direction: "desc").Items
                    .Select(group => group.Key).SequenceEqual(["Two", "one", "", "One"])
                && store.ListGroups("alice", null, "series", sort: "pageCount").Items.SequenceEqual(groups.Items),
                "Text MIN/MAX ignored NOCASE or numeric arrangement lost stable label/key ties.");
            store.EditMetadata(ids["Gamma"], new(new() { ["Title"] = "Gamma" }));
            store.EditMetadata(ids["Delta"], new(new() { ["Title"] = "Delta" }));

            var manual = store.CreateList("alice", new("Manual", "manual", "all", []));
            store.AddListBooks("alice", manual.Id, [ids["Alpha"], ids["Beta"], ids["Gamma"], ids["Delta"]]);
            var smart = store.CreateList("alice", new("Smart", "smart", "all", [new("Publisher", "equals", "Press")]));
            store.SaveProgress("alice", ids["Alpha"], new(0, true));
            store.SaveProgress("alice", ids["Delta"], new(0, false));
            foreach (var listId in new[] { manual.Id, smart.Id })
            {
                var intersection = store.ListGroups("alice", "marker", "series", 2, 1,
                    status: "unread", publisher: "Press", listId: listId);
                Check(intersection.Items.All(group => group.Count == 1)
                    && intersection.Total == (listId == manual.Id ? 1 : 2), "Group filters ran after pagination.");
                var selected = intersection.Items.Single();
                Check(store.ListBooks("alice", "marker", pageSize: 1, status: "unread", publisher: "Press",
                    listId: listId, groupBy: "series", groupKey: selected.Key).Total == selected.Count,
                    "Filtered group drilldown lost a list/search/status/publisher intersection.");
                Reject<KeyNotFoundException>(() => store.ListGroups("bob", null, "series", listId: listId));
                Reject<KeyNotFoundException>(() => store.ListBooks("bob", null, listId: listId, groupBy: "series", groupKey: "One"));
            }
            Check(store.ListGroups("alice", null, "series", status: "completed").Items.Single().CoverId == ids["Alpha"]
                && store.ListGroups("bob", null, "series", status: "completed").Total == 0,
                "Grouped progress leaked across users.");
            var privateStack = store.ListGroups("alice", null, "series", sectionBy: "publisher",
                sectionKey: "Press").Items.Single(group => group.Key == "One");
            Check(privateStack is { Count: 2, Completed: 1, Unread: 1, InProgress: 0, CompletionPercent: 50 }
                && store.ListGroups("bob", null, "series", sectionBy: "publisher").Items.All(group => group.Completed == 0
                    && group.InProgress == 0 && group.Unread == group.Count),
                "Sectioned stack counts were paged early or leaked private progress.");
            var filteredSections = store.ListGroups("alice", "marker", "series", pageSize: 1, status: "unread",
                listId: manual.Id, sectionBy: "publisher", sort: "number");
            Check(filteredSections.Total == 2 && filteredSections.Items.Single().CoverId == ids["Gamma"]
                && filteredSections.Sections.Single() is { Key: "Other", Start: 0, Count: 1 }
                && store.ListBooks("alice", "marker", pageSize: 1, status: "unread", listId: manual.Id,
                    sectionBy: "publisher", sectionKey: "Other", groupBy: "series", groupKey: "Two").Items.Single().Id == ids["Gamma"],
                "Search/list/status filtering happened after section ordering, aggregation or paging.");
            Check(store.ListGroups("alice", null, "series", listId: manual.Id, sort: "list")
                    .Items.Select(group => group.Key).SequenceEqual(["One", "Two"])
                && store.ListGroups("alice", null, "series", listId: manual.Id, sort: "list", direction: "desc")
                    .Items.Select(group => group.Key).SequenceEqual(["Two", "One"]),
                "Manual-list stack arrangement did not use minimum/maximum member positions.");
            store.SaveRating("alice", ids["Gamma"], new(5));
            Check(store.ListGroups("alice", null, "series", sectionBy: "publisher", rating: 5).Items.Single().Count == 1
                && store.ListGroups("bob", null, "series", sectionBy: "publisher", rating: 5).Total == 0
                && store.ListGroups("alice", null, "series", sort: "personalRating", direction: "desc").Items[0].Key == "Two",
                "Sectioned ratings leaked between accounts or failed numeric arrangement.");
            Reject<KeyNotFoundException>(() => store.ListGroups("bob", null, "series", listId: manual.Id, sectionBy: "publisher"));
            Reject<KeyNotFoundException>(() => store.ListBooks("bob", null, listId: smart.Id, sectionBy: "publisher", sectionKey: "Press"));
            store.EditMetadata(ids["Delta"], new(new() { ["Series"] = "One" }));
            Check(store.ListGroups("alice", null, "series").Items.Single(group => group.Key == "One").Count == 3
                && store.ListBooks("alice", null, groupBy: "series", groupKey: "Two").Items.Single().Id == ids["Gamma"],
                "Metadata edits did not update groups and drilldown immediately.");
            const string sqlLikeKey = "' OR 1=1 --";
            store.EditMetadata(ids["Gamma"], new(new() { ["Series"] = sqlLikeKey }));
            Check(store.ListBooks("alice", null, groupBy: "series", groupKey: sqlLikeKey).Items.Single().Id == ids["Gamma"]
                && store.ListGroups("alice", null, "series").Items.Any(group => group.Key == sqlLikeKey),
                "Group keys were interpreted as SQL.");
            store.EditMetadata(ids["Gamma"], new(new() { ["Publisher"] = sqlLikeKey }));
            Check(store.ListBooks("alice", null, groupBy: "series", groupKey: sqlLikeKey,
                    sectionBy: "publisher", sectionKey: sqlLikeKey).Items.Single().Id == ids["Gamma"]
                && store.ListGroups("alice", null, "series", sectionBy: "publisher", sectionKey: sqlLikeKey).Total == 1
                && store.ListBooks("alice", null, sectionBy: "publisher", sectionKey: "unknown").Total == 0
                && store.ListGroups("alice", null, "series", sectionBy: "publisher", sectionKey: "unknown").Sections.Count == 0,
                "Section keys were interpreted as SQL or unknown sections did not stay empty.");

            File.Delete(Path.Combine(firstRoot, "Stacks", "Alpha.cbz"));
            File.Delete(Path.Combine(firstRoot, "Empty.cbz"));
            Directory.Move(secondRoot, secondRoot + "-offline");
            store.ScanAsync(default).GetAwaiter().GetResult();
            groups = store.ListGroups("alice", null, "series");
            Check(groups.Items.Single(group => group.Key == "One").CoverId == ids["Beta"]
                && groups.Items.Single(group => group.Key == "").CoverId is null
                && groups.Items.Single(group => group.Key == "one").CoverId is null,
                "Groups selected missing/unavailable covers or hid catalog members.");
            foreach (var availability in new[] { "available", "missing", "unavailable" })
            {
                var filtered = store.ListGroups("alice", null, "series", availability: availability);
                Check(filtered.Items.Sum(group => group.Count) == store.ListBooks("alice", null, availability: availability).Total,
                    "Availability groups disagreed with the flat catalog.");
                foreach (var group in filtered.Items)
                    Check(store.ListBooks("alice", null, availability: availability, groupBy: "series", groupKey: group.Key).Total == group.Count,
                        "Availability drilldown disagreed with its group.");
            }
            Check(store.ListGroups("alice", null, "series", availability: "missing").Items.Sum(group => group.Count) == 2
                && store.ListGroups("alice", null, "series", availability: "unavailable").Items.Single().Key == "one",
                "Missing and unavailable source grouping failed.");
            Reject<ArgumentException>(() => store.ListGroups("alice", null, "none"));
            Reject<ArgumentException>(() => store.ListGroups("alice", null, "series; DROP TABLE books"));
            Reject<ArgumentException>(() => store.ListGroups("alice", null, "series", page: 0));
            Reject<ArgumentException>(() => store.ListGroups("alice", null, "series", pageSize: 101));
            Reject<ArgumentException>(() => store.ListGroups("alice", null, "series", availability: "unknown"));
            Reject<ArgumentException>(() => store.ListBooks("alice", null, availability: "unknown"));
            Reject<ArgumentException>(() => store.ListBooks("alice", null, groupBy: "unknown"));
            Reject<ArgumentException>(() => store.ListBooks("alice", null, groupKey: ""));
            Reject<ArgumentException>(() => store.ListBooks("alice", null, sectionBy: "publisher; DROP TABLE books"));
            Reject<ArgumentException>(() => store.ListGroups("alice", null, "series", sectionBy: "unknown"));
            Reject<ArgumentException>(() => store.ListBooks("alice", null, sectionKey: ""));
            Reject<ArgumentException>(() => store.ListGroups("alice", null, "series", sectionKey: ""));
            Reject<ArgumentException>(() => store.ListGroups("alice", null, "series", sort: "number; DROP TABLE books"));
            Reject<ArgumentException>(() => store.ListGroups("alice", null, "series", direction: "sideways"));

            var issueNumbers = new[] { "30", "36", "38", "39", "038", "Annual" };
            for (var index = 0; index < all.Count; index++)
                store.EditMetadata(all[index].Id, new(new() { ["Series"] = "Gap checks", ["Number"] = issueNumbers[index] }));
            var numbered = store.ListBooks("alice", null, sort: "number", groupBy: "series", groupKey: "Gap checks");
            Check(numbered.Total == 6 && numbered.MissingIssues.Select(gap => (gap.First, gap.Last))
                .SequenceEqual([(31, 35), (37, 37)]), "Series gaps lost whole numbers or treated variants as missing.");
            var report = store.ListMissingIssues();
            Check(report.Total == 2 && report.Items.SequenceEqual(new[] {
                new MissingIssueReport("Gap checks", "library", 31, 35),
                new MissingIssueReport("Gap checks", "library", 37, 37) }),
                "Maintenance report disagreed with the series placeholders.");
            Check(store.ListMissingIssues(1, 1).Items.Single() == report.Items[0]
                && store.ListMissingIssues(int.MaxValue, 1).Items.Single() == report.Items[1]
                && store.ListMissingIssues(int.MaxValue, 1).Page == 2,
                "Missing issue report pagination skipped or duplicated a gap.");
            Reject<ArgumentException>(() => store.ListMissingIssues(0));
            Reject<ArgumentException>(() => store.ListMissingIssues(pageSize: 101));
            store.SetMissingIssueIgnored("Gap checks", true);
            store.SetMissingIssueIgnored("Gap checks", true);
            Check(store.ListMissingIssues().Total == 0
                && store.ListMissingIssues(int.MaxValue, 1, ignored: true).Items.Single() == report.Items[1]
                && store.ListMissingIssues(int.MaxValue, 1, ignored: true).Page == 2,
                "Ignoring a series failed to hide every gap or broke ignored-report pagination.");
            var reopened = new LibraryStore(Path.Combine(directory, "groups.sqlite"), [firstRoot, secondRoot]);
            Check(reopened.ListMissingIssues().Total == 0 && reopened.ListMissingIssues(ignored: true).Total == 2
                && reopened.ListBooks("alice", null, groupBy: "series", groupKey: "Gap checks").MissingIssues.Count == 2,
                "Ignored series did not persist or changed normal series browsing.");
            store.SetMissingIssueIgnored("gap checks", false);
            Check(store.ListMissingIssues().Total == 0, "Ignore matching must preserve exact series identity.");
            store.SetMissingIssueIgnored("Gap checks", false);
            Check(store.ListMissingIssues().Items.SequenceEqual(report.Items) && store.ListMissingIssues(ignored: true).Total == 0,
                "Unignore did not restore every series gap.");
            Reject<ArgumentException>(() => store.SetMissingIssueIgnored("", true));
            Reject<ArgumentException>(() => store.SetMissingIssueIgnored(new string('x', 16385), true));
            foreach (var direction in new[] { "asc", "desc" })
            {
                var gaps = Enumerable.Range(1, 6).SelectMany(page => store.ListBooks("alice", null,
                    page, 1, "number", direction, groupBy: "series", groupKey: "Gap checks").MissingIssues).ToArray();
                Check(gaps.Length == 2 && gaps.Any(gap => gap.First == 37 && gap.Last == 37),
                    "Page boundaries lost or duplicated missing issues.");
                foreach (var gap in gaps)
                {
                    var anchor = numbered.Items.Single(book => book.Id == gap.BeforeBookId);
                    Check(int.Parse(anchor.Number) == (direction == "asc" ? gap.Last + 1 : gap.First - 1),
                        "Missing issues were attached to the wrong side of descending or ascending results.");
                }
            }
            var filteredGaps = store.ListBooks("alice", all[2].Title, sort: "number",
                groupBy: "series", groupKey: "Gap checks").MissingIssues;
            Check(filteredGaps.All(gap => gap.First == 37 && gap.Last == 37),
                "Search-hidden issues were falsely marked missing.");
            Check(store.ListBooks("alice", null).MissingIssues.Count == 0
                && store.ListBooks("alice", null, groupBy: "publisher", groupKey: "Press").MissingIssues.Count == 0,
                "Placeholders escaped series drilldown.");
            store.EditMetadata(all[2].Id, new(new() { ["Number"] = "2147483647" }));
            store.EditMetadata(all[4].Id, new(new() { ["Number"] = "38.5" }));
            var largeGap = store.ListBooks("alice", null, groupBy: "series", groupKey: "Gap checks").MissingIssues;
            Check(largeGap.Any(gap => gap.First == 40 && gap.Last == 2147483646)
                && largeGap.Any(gap => gap.First == 37 && gap.Last == 38),
                "Large gaps overflowed or decimal issue numbers were treated as whole numbers.");

            var scoped = new LibraryStore(Path.Combine(directory, "groups.sqlite"), [secondRoot],
                Path.Combine(firstRoot, "Other"), Path.Combine(firstRoot, "Stacks"));
            foreach (var location in new[] { "library", "archived", "unsorted" })
            {
                var area = scoped.ListGroups("alice", null, "series", sectionBy: "folder", location: location);
                Check(area.Items.Sum(group => group.Count) == 2
                    && area.Sections.All(section => !section.Key.Contains(directory)),
                    "Sectioned folder stacks ignored location boundaries or exposed a root path.");
                foreach (var section in area.Sections)
                foreach (var stack in area.Items.Skip(section.Start).Take(section.Count))
                    Check(scoped.ListBooks("alice", null, groupBy: "series", groupKey: stack.Key,
                        sectionBy: "folder", sectionKey: section.Key, location: location).Total == stack.Count,
                        "Sectioned location drilldown escaped its area.");
                Check(scoped.ListGroups("alice", null, "series", sectionBy: "folder", location: location,
                    listId: manual.Id).Items.Sum(group => group.Count) == (location == "library" ? 0 : 2),
                    "Private-list section grouping bypassed location filtering.");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
