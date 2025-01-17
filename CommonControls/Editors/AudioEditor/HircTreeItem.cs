﻿using CommonControls.FileTypes.Sound.WWise;
using System.Collections.Generic;

namespace CommonControls.Editors.AudioEditor
{
    public class HircTreeItem
    {
        public string DisplayName { get; set; } = string.Empty;
        public HircItem Item { get; set; }


        public List<HircTreeItem> Children { get; set; } = new List<HircTreeItem>();
        public HircTreeItem Parent { get; set; } = null;
    }

    public class HircTreeItemUtil
    {
        public HircTreeItem GetRootParent(HircTreeItem item)
        {
            if (item.Parent == null)
                return item;
            return GetRootParent(item.Parent);
        }
    }
}
