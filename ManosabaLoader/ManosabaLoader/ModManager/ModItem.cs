using System;
using System.Text.Json;

namespace ManosabaLoader.ModManager
{
    public class ModItem
    {
        public class ModCharacter
        {
            public string ActorId { get; set; } = "Taffy";
            public string DisplayName { get; set; } = "";

        }
        public class ModDescription
        {
            const string DefaultAuthor = "佚名";
            const string DefaultDescription = "无内容。";
            public string Name { get; set; } = "";
            public string Description { get; set; } = DefaultDescription;
            public string Author { get; set; } = DefaultAuthor;
            public string Version { get; set; } = "1.0.0";
            public string Enter { get; set; } = "";
            public ModCharacter[] Characters { get; set; } = [];
        }
        class ModItemException : Exception
        {
            public ModItemException(string ex) : base(ex) { }
        }

        bool valid = false;
        ModDescription description = null;

        public bool Valid
        {
            get => valid;
            internal set => valid = value;
        }

        public ModDescription Description
        {
            get => description;
            internal set => description = value;
        }

        internal ModItem()
        {
            
        }
        
        public ModItem(string path,string config) 
        {
            try
            {
                description = JsonSerializer.Deserialize<ModDescription>(config);
                if (description == null || description.Name == "" || description.Enter == "")
                {
                    throw new ModItemException("config format error.");
                }
                valid = true;
            }
            catch (Exception ex) 
            {
                ModManager.ModManagerLogError(string.Format("Load {0} failed!", path));
                ModManager.ModManagerLogError(ex.ToString());
            }
        }
    }
}
