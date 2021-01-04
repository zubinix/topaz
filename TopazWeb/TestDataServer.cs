using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using Topaz.Extra;

namespace Topaz.TestDataServer
{
    public class TestDataServer
    {
        private NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        private string tds_server_url = null;

        private string base_url = "/cgi-bin/lockme.cgi";

        private RestClient tds_client = null;

        public enum PropertyOps
        {
            None,
            Union,
            InvertedUnion
        }

        // helper funcs
        private Func<String, String> RemovePathType = ((x) => x.Substring(x.IndexOf(':') + 1));

        private static string ToEnumString<T>(T type)
        {
            var enumType = typeof(T);
            var name = Enum.GetName(enumType, type);
            var enumMemberAttribute_tmp = ((EnumMemberAttribute[])enumType.GetField(name).GetCustomAttributes(typeof(EnumMemberAttribute), true));
            var stringName = name;
            if (enumMemberAttribute_tmp.Length > 0)
            {
                var enumMemberAttribute = enumMemberAttribute_tmp.Single();
                stringName = enumMemberAttribute.Value;
            }

            return stringName;
        }

        public TestDataServer(string url)
        {
            tds_server_url = url + base_url;
            tds_client = new RestClient(tds_server_url);
        }

        public void WarmUpServer()
        {
            int attempts = 3;

            try
            {
                while (attempts-- > 0)
                {
                    ListAllLocks();
                    Logger.Info("TDS Server is ready");
                    break;
                }
            }
            catch
            {
                Logger.Warn("Failed attempt at waking up TDS server");
            }
        }

        public bool CreateLock(string lockname)
        {
            string method = "create";

            // remove '.lock' suffix as its added by the server
            lockname = lockname.Trim(".lock".ToCharArray());

            var resp = tds_client.Get(new RestRequest().AddParameter("method", method).AddParameter("name", lockname));

            HttpStatusCode status = resp.StatusCode;

            if (resp.StatusCode != HttpStatusCode.OK)
            {
                Logger.Warn($"Could not create lock for '{lockname}', returned {resp.StatusCode}");
            }

            return resp.StatusCode == HttpStatusCode.OK;
        }

        public void DeleteLock(string lockname)
        {
            string method = "delete";

            var resp = tds_client.Get(new RestRequest().AddParameter("method", method).AddParameter("name", lockname));

            if (resp.StatusCode != HttpStatusCode.OK)
            {
                Logger.Warn($"Could not delete lock '{lockname}', returned {resp.StatusCode}");
            }
        }

        public List<string> ListAllLocks()
        {
            string method = "list";

            var resp = tds_client.Get(new RestRequest().AddParameter("method", method));

            if (resp.StatusCode != HttpStatusCode.OK)
            {
                Logger.Warn($"Could not retreive list of locks, returned {resp.StatusCode}");
            }

            List<string> locks = JsonConvert.DeserializeObject<List<string>>(resp.Content);

            return locks;
        }

        // NB: use 'FindLocks' if locks contain no data e.g. IN_USE_ locks
        public List<string> FindLocks_with_Properties(string lockRegex, string[] properties, PropertyOps op = PropertyOps.None)
        {
            string method = "findprop";
            List<List<string>> returned_locks = new List<List<string>>();
            List<string> required_locks = new List<string>();

            // add environment to lock regex
            lockRegex = lockRegex + ($".*_{Utils.GetSelectedEnvironment()}");

            foreach (string prop in properties)
            {
                var resp = tds_client.Get(new RestRequest().AddParameter("method", method).AddParameter("property", prop).AddParameter("pattern", lockRegex));

                if (resp.StatusCode != HttpStatusCode.OK)
                {
                    Logger.Warn($"Could not get data for property '{prop}' and pattern '{lockRegex}', returned {resp.StatusCode}");
                }

                List<string> locks = JsonConvert.DeserializeObject<List<string>>(resp.Content);
                returned_locks.Add(locks);
            }
            
            if (properties.Length == 1 && op != PropertyOps.None)
            {
                throw new Exception($"Cannot use {PropertyOps.Union} witha single regex");
            }

            if (returned_locks.Count > 1)
            {
                if (op == PropertyOps.Union)
                {
                    ; // not implemented
                }
                else if (op == PropertyOps.InvertedUnion)
                {
                    // remove locks that are common to all lists
                    var common_locks = returned_locks[0];
                    var all_locks = returned_locks[0];

                    // find common locks
                    for(int i=1; i<returned_locks.Count; i++)
                    {
                        common_locks = common_locks.Intersect(returned_locks[i]).ToList();
                        all_locks = all_locks.Union(returned_locks[i]).ToList();
                    }

                    // remove common elements
                    for(int i=0; i<all_locks.Count; i++)
                    {
                        if (!common_locks.Contains(all_locks[i]))
                        {
                            required_locks.Add(all_locks[i]);
                        }
                    }

                }
                
            }
            else
            {
                required_locks = returned_locks[0];
            }

            return required_locks;
        }

        public List<string> FindLocks(Regex lockRegex)
        {
            List<string> matchedLocks = null;
            string current_env = $"_{Utils.GetSelectedEnvironment()}";

            List<string> allLocks = ListAllLocks();

            // extract locks for our environment
            List<string> ourLocks = allLocks.FindAll(lo => lo.Contains(current_env));

            // find the locks we want
            matchedLocks = ourLocks.FindAll(lo => lockRegex.IsMatch(lo));

            return matchedLocks;
        }

        public string GetLockData(string lockname)
        {
            string method = "getdata";

            var resp = tds_client.Get(new RestRequest().AddParameter("method", method).AddParameter("name", lockname));

            if (resp.StatusCode != HttpStatusCode.OK)
            {
                Logger.Warn($"Could not get data for lock for '{lockname}', returned {resp.StatusCode}");
            }

            return resp.Content;
        }

        public bool SetLockData(string lockname, string data)
        {
            string method = "setdata";

            // append 'lockname' and 'method' to be used by server
            JObject m1 = JObject.Parse($"{{'body' : '{data}'}}");
            JObject m2 = JObject.Parse(
               $"{{ 'name' : '{lockname}'," +
               $" 'method' : '{method}' }}");

            m1.Merge(m2);

            // remove quotes(") around 'body' data else will be considered a single string by JSON formatters
            string payload = Regex.Unescape(m1.ToString().Replace("\"{", "{").Replace("}\"", "}"));

            //         var resp = tds_client.Get(new RestRequest().AddParameter("method", method).AddParameter("name", lockname).AddBody(m1.ToString()));
            var req = new RestRequest("", Method.POST);
            req.AddParameter("application/json; charset=utf-8", payload, ParameterType.RequestBody);
            var resp = tds_client.Execute(req);

            if (resp.StatusCode != HttpStatusCode.OK)
            {
                Logger.Warn($"Could not set data against existing lock for '{lockname}', returned {resp.StatusCode}");
            }

            return resp.StatusCode == HttpStatusCode.OK;
        }

        public bool AddPropertyToLockData(string lockname, string property, string value)
        {
            var data = GetLockData(lockname);

            // append 'lockname' and 'method' to be used by server
            JObject m1 = JObject.Parse($"{data}");
            JObject m2 = JObject.Parse($"{{ '{property}' : '{value}' }}");

            m1.Merge(m2);

            // remove quotes(") around 'body' data else will be considered a single string by JSON formatters
            string payload = Regex.Unescape(m1.ToString().Replace("\"{", "{").Replace("}\"", "}"));

            bool res = SetLockData(lockname, payload);

            return res;
        }

        public void DetermineLockAge(string lockname)
        {
            string url = tds_server_url + "?age=";

        }

        public void WaitForLockCreate(string lockname)
        {

        }

        public void WaitForLockDelete(string lockname)
        {

        }
    }
}
