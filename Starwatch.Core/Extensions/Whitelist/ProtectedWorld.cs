﻿using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Starwatch.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace Starwatch.Extensions.Whitelist
{
    public class ProtectedWorld
    {
        public World World { get; private set; }
        public HashSet<string> AccountList { get; private set; }
        public bool AllowAnonymous { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public WhitelistMode Mode { get; set; }

        public ProtectedWorld(World world, WhitelistMode mode, bool allowAnonymous = false)
        {
            World = world;
            AccountList = new HashSet<string>();
            Mode = mode;
            AllowAnonymous = allowAnonymous;
        }
        
        /// <summary>
        /// Checks to make sure if the account is allowed on this world and will return true if it is.
        /// </summary>
        /// <param name="account"></param>
        /// <returns></returns>
        public bool ValidateAccount(string account)
        {
            if (account == null || account == Account.Annonymous) return AllowAnonymous;
            bool withinListing = AccountList.Contains(account);
            return Mode == WhitelistMode.Whitelist ? withinListing : !withinListing;
        }

    }
}
