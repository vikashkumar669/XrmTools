using System;
using System.Linq;
using System.Xml.Linq;
using PluginFailureCorrelationExplorer.Data;
using PluginFailureCorrelationExplorer.Models;
using static PluginFailureCorrelationExplorer.Tests.Samples;

namespace PluginFailureCorrelationExplorer.Tests
{
    public static class FetchXmlTests
    {
        private static XElement Parse(string fetch) => XElement.Parse(fetch);

        private static string[] Attributes(XElement fetch) =>
            fetch.Element("entity").Elements("attribute").Select(a => (string)a.Attribute("name")).ToArray();

        private static XElement Condition(XElement fetch, string attribute) =>
            fetch.Descendants("condition").FirstOrDefault(c => (string)c.Attribute("attribute") == attribute);

        [Test]
        public static void Summary_query_excludes_large_text_and_configuration_columns()
        {
            var attrs = Attributes(Parse(TraceFetchXml.Summary(Query(), 1, null, 5000)));
            Assert.True(attrs.Contains("correlationid") && attrs.Contains("requestid") && attrs.Contains("performanceexecutionduration"));
            foreach (var excluded in new[] { "exceptiondetails", "messageblock", "configuration", "secureconfiguration" })
                Assert.False(attrs.Contains(excluded), excluded + " must not be in the summary query");
        }

        [Test]
        public static void Filters_are_applied_server_side()
        {
            var q = Query();
            q.Exceptions = ExceptionFilter.WithException;
            q.MessageName = " Update ";
            q.PrimaryEntity = "account";
            q.PluginType = "Account";
            q.CorrelationId = Guid.Parse("11111111-2222-3333-4444-555555555555");
            q.RequestId = Guid.Parse("66666666-7777-8888-9999-000000000000");
            q.MinDurationMs = 1500;
            var f = Parse(TraceFetchXml.Summary(q, 1, null, 100));
            Assert.Equal("not-null", (string)Condition(f, "exceptiondetails").Attribute("operator"));
            Assert.Equal("Update", (string)Condition(f, "messagename").Attribute("value"));
            Assert.Equal("account", (string)Condition(f, "primaryentity").Attribute("value"));
            Assert.Equal("%Account%", (string)Condition(f, "typename").Attribute("value"));
            Assert.Equal("11111111-2222-3333-4444-555555555555", (string)Condition(f, "correlationid").Attribute("value"));
            Assert.Equal("66666666-7777-8888-9999-000000000000", (string)Condition(f, "requestid").Attribute("value"));
            Assert.Equal("ge", (string)Condition(f, "performanceexecutionduration").Attribute("operator"));
            Assert.Equal("1500", (string)Condition(f, "performanceexecutionduration").Attribute("value"));
            Assert.Equal("2026-10-20T08:00:00Z", (string)Condition(f, "createdon").Attribute("value"));
        }

        [Test]
        public static void Without_exception_filter_uses_null()
        {
            var q = Query();
            q.Exceptions = ExceptionFilter.WithoutException;
            Assert.Equal("null", (string)Condition(Parse(TraceFetchXml.Summary(q, 1, null, 10)), "exceptiondetails").Attribute("operator"));
            q.Exceptions = ExceptionFilter.All;
            Check.Null(Condition(Parse(TraceFetchXml.Summary(q, 1, null, 10)), "exceptiondetails"));
        }

        [Test]
        public static void Values_and_like_wildcards_are_escaped()
        {
            var q = Query();
            q.PluginType = "My_Plugin%[x]\"<&>";
            q.MessageName = "a\"b<c>&d";
            var xml = TraceFetchXml.Summary(q, 1, null, 10);
            var f = Parse(xml);   // parses: XML special characters are escaped
            Assert.Equal("%My[_]Plugin[%][[]x]\"<&>%", (string)Condition(f, "typename").Attribute("value"));
            Assert.Equal("a\"b<c>&d", (string)Condition(f, "messagename").Attribute("value"));
        }

        [Test]
        public static void Paging_count_and_cookie()
        {
            var f = Parse(TraceFetchXml.Summary(Query(), 3, "<cookie page=\"2\"><x last=\"1\" /></cookie>", 99999));
            Assert.Equal("5000", (string)f.Attribute("count"));
            Assert.Equal("3", (string)f.Attribute("page"));
            Assert.Equal("<cookie page=\"2\"><x last=\"1\" /></cookie>", (string)f.Attribute("paging-cookie"));
            Assert.Equal("1", (string)Parse(TraceFetchXml.Summary(Query(), 1, null, 0)).Attribute("count"));
        }

        [Test]
        public static void Exception_query_is_limited_to_the_retrieved_window()
        {
            var q = Query();
            var f = Parse(TraceFetchXml.Exceptions(q, T0.AddHours(5), 1, null, 5000));
            Assert.Equal("plugintracelogid,exceptiondetails", string.Join(",", Attributes(f)));
            Assert.Equal("2026-10-20T14:00:00Z", (string)Condition(f, "createdon").Attribute("value"));
            Assert.Equal("not-null", (string)Condition(f, "exceptiondetails").Attribute("operator"));
            // The oldest retrieved row can't move the window before the query's own start
            Assert.Equal("2026-10-20T08:00:00Z", (string)Condition(Parse(TraceFetchXml.Exceptions(q, T0.AddDays(-9), 1, null, 10)), "createdon").Attribute("value"));
        }

        [Test]
        public static void Id_queries_are_batched_and_validated()
        {
            var ids = Enumerable.Range(0, 450).Select(_ => Guid.NewGuid()).ToList();
            var batches = TraceFetchXml.Batches(ids).ToList();
            Assert.Equal(3, batches.Count);
            Assert.Equal(200, batches[0].Count);
            Assert.Equal(50, batches[2].Count);
            var f = Parse(TraceFetchXml.ExceptionsByIds(batches[0]));
            Assert.Equal(200, f.Descendants("value").Count());
            Check.Throws<ArgumentException>(() => TraceFetchXml.ExceptionsByIds(ids));
            Check.Throws<ArgumentException>(() => TraceFetchXml.ExceptionsByIds(new Guid[0]));
            var detail = Attributes(Parse(TraceFetchXml.Detail(ids[0])));
            Assert.True(detail.Contains("messageblock") && !detail.Contains("secureconfiguration"));
        }

        [Test]
        public static void Step_query_links_type_and_assembly()
        {
            var f = Parse(TraceFetchXml.Steps(new[] { Guid.NewGuid(), Guid.NewGuid() }));
            Assert.Equal("sdkmessageprocessingstep", (string)f.Element("entity").Attribute("name"));
            var links = f.Descendants("link-entity").Select(l => (string)l.Attribute("name")).ToList();
            Assert.Equal("plugintype,pluginassembly", string.Join(",", links));
        }

        [Test]
        public static void Query_validation()
        {
            var q = Query();
            Assert.Equal(0, q.Validate().Count);
            q.ToUtc = q.FromUtc;
            q.ResultLimit = 0;
            q.MinDurationMs = -1;
            Assert.Equal(3, q.Validate().Count);
            q = Query();
            q.ResultLimit = TraceQuery.MaxResultLimit + 1;
            Assert.Equal(1, q.Validate().Count);
        }

        [Test]
        public static void Query_description_lists_active_filters()
        {
            var q = Query();
            Assert.Equal("none", q.Describe());
            q.Exceptions = ExceptionFilter.WithException;
            q.PrimaryEntity = "account";
            Assert.Equal("with exception; table = account", q.Describe());
        }
    }
}
