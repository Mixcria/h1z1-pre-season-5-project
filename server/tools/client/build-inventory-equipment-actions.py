#!/usr/bin/env python3
"""Build one-click saved skins for looted equipment and authoritative hood actions."""
import importlib.util
import json
from pathlib import Path
import re

spec = importlib.util.spec_from_file_location('loot_hood', Path(__file__).with_name('build-inventory-loot-hood.py'))
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)
builder = base.builder


def transform(source, catalog):
    source = base.transform(source, catalog)
    item_map, category_slots = {}, {}
    body_to_loadout = {1: 11, 2: 16, 3: 10, 4: 14, 5: 13, 10: 12, 11: 25, 28: 28, 29: 29, 100: 38, 101: 30}
    apparel = catalog.split('IReadOnlyList<AugustSkinCatalogEntry> Apparel =', 1)[1].split('];', 1)[0]
    weapons = catalog.split('IReadOnlyList<AugustSkinCatalogEntry> Weapons =', 1)[1].split('];', 1)[0]
    pattern = r'new\((\d+), (\d+), (\d+), (\d+), "([^"]+)", "([^"]+)"'
    apparel_rows = list(re.findall(pattern, apparel))
    for category, reward, _, slot, _, _ in apparel_rows + list(re.findall(pattern, weapons)):
        item_map[category] = item_map[reward] = int(category)
    for category, _, _, slot, _, _ in apparel_rows:
        category_slots[category] = body_to_loadout[int(slot)]
    item_map['2425'] = 10
    # Match the server's unambiguous loot-only helmet/backpack aliases.
    meshes = (builder.ROOT / 'src/Cranberry.Zone/Appearance/AugustWornMeshCatalog.g.cs').read_text(encoding='utf-8')
    for item, male, female in re.findall(r'new\((\d+), \d+, \d+, "([^"]+)", "([^"]+)"', meshes):
        if item in item_map:
            continue
        categories = {int(c) for c, _, _, slot, m, f in apparel_rows
                      if int(slot) in (1, 10) and m.lower() == male.lower() and f.lower() == female.lower()}
        if len(categories) == 1:
            item_map[item] = categories.pop()
    assert len(item_map) > 600 and len(category_slots) >= 10
    source, count = re.subn(r'private static const SKIN_CATEGORY_BY_ITEM:Object = \{.*?\};',
        'private static const SKIN_CATEGORY_BY_ITEM:Object = ' + json.dumps(item_map, separators=(',', ':')) + ';',
        source, flags=re.DOTALL)
    assert count == 1
    source = re.sub(r'      private static const SKIN_CATEGORY_BY_ACCOUNT:Object = \{.*?\};', '', source, flags=re.DOTALL)
    source = source.replace('      private var m_skinCategory:int = 0;',
        '      private static const APPAREL_SLOT_BY_CATEGORY:Object = ' + json.dumps(category_slots, separators=(',', ':'))
        + ';\n      private var m_skinCategory:int = 0;')
    source = source.replace('   import flash.utils.getDefinitionByName;\n', '')
    source = source.replace('      private var m_skinSource:Object;', '')
    source = source.replace('      private var m_skinMode:Boolean = false;', '')
    source = source.replace('         this.stopSkinChoices();', '')
    source = builder.method(source, 'stopSkinChoices', '')
    source = builder.method(source, 'refreshSkinChoices', '')
    source = base.replace_once(source, '''         if(this.m_skinMode)
         {
            this.refreshSkinChoices();
            return;
         }
''', '')
    source = source.replace('this.m_skinMode ? Math.max(this.m_regularWidth,340) : this.m_regularWidth', 'this.m_regularWidth')
    source = source.replace('ownedWeaponCategory', 'equippedSkinCategory').replace('"v5 item="', '"v7 item="')
    source = builder.method(source, 'equippedSkinCategory', '''
      private function equippedSkinCategory(item:IItemData) : int
      {
         if(item == null) { return 0; }
         // The server publishes only equipped items whose appearance differs from the
         // player's owned preset. GUIDs remain strings, including above Number's range.
         var targets:String = ";" + String(UIBindingSystem.GetStringHashValue("Cranberry.Inventory.SkinTargets","")) + ";";
         if(targets.indexOf(";" + item.guid + ";") < 0) { return 0; }
         var rows:Array = this.currentLoadoutRows();
         var index:int = 0;
         while(index < rows.length)
         {
            var row:Object = rows[index];
            if(String(row.ItemGuid) == item.guid)
            {
               var category:int = int(SKIN_CATEGORY_BY_ITEM[String(row.ItemId)]);
               var slot:int = int(row.ContainerSlotId);
               var apparelSlot:int = int(APPAREL_SLOT_BY_CATEGORY[String(category)]);
               if(apparelSlot > 0) { return slot == apparelSlot ? category : 0; }
               return slot == 1 || slot == 2 || slot == 4 ? category : 0;
            }
            index++;
         }
         return 0;
      }
''')
    source = builder.method(source, 'handleListItemClick', '''
      protected function handleListItemClick(param1:ListEvent) : void
      {
         if(this.m_item == null) { return; }
         var action:ItemActionData = this.m_list.dataProvider.requestItemAt(param1.index) as ItemActionData;
         if(action == null) { return; }
         if(action.id == 88)
         {
            if(this.equippedSkinCategory(this.m_item) > 0)
            {
               UIBindingSystem.DispatchWallOfData("Cranberry.Inventory","skin:" + this.m_item.guid + ":selected");
            }
            this.setItem(null);
            return;
         }
         if(action.id == 96 || action.id == 97)
         {
            var hood:int = this.hoodieContext(this.m_item);
            if((action.id == 96 && hood == 1) || (action.id == 97 && hood == 2))
            {
               UIBindingSystem.DispatchWallOfData("Cranberry.Inventory","hood:" + this.m_item.guid + ":" + (action.id == 96 ? "1" : "0"));
            }
            this.setItem(null);
            return;
         }
         ActionManager.getInstance().doAction(this.m_item,action);
         this.setItem(null);
      }
''')
    source = source.replace('         this.m_list.dataProvider = new DataProvider(actions);',
        '         this.m_list.scrollPosition = 0;\n         this.m_list.dataProvider = new DataProvider(actions);')
    assert not any(s in source for s in ['m_skinMode', 'm_skinSource', 'AccountInventory', 'No skins owned', 'refreshSkinChoices'])
    return source


if __name__ == '__main__':
    builder.SOURCE_SHA = base.SOURCE_SHA256
    builder.transform = transform
    builder.main()
