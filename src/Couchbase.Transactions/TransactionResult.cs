﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Couchbase.Core;
using Couchbase.Query;
using DnsClient.Internal;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Couchbase.Transactions
{
    public class TransactionResult
    {
        [JsonIgnore]
        public ILogger<Transactions>? Logger { get; internal set; }

        public string? TransactionId { get; internal set; }

        public bool UnstagingComplete { get; internal set; }

        public override string ToString()
        {
            return JObject.FromObject(this).ToString();
        }
    }
}
