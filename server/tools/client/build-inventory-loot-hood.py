#!/usr/bin/env python3
"""Upgrade the installed weapon Skin menu with item targeting and worn-hoodie actions.

Only ItemActionMenu changes. Reuse August's RequestUseItem binding for the target and
hood actions, and the existing cached account-skin selector for the selected cosmetic.
"""
import importlib.util
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
SOURCE_SHA256 = '88d415d47d56fd0d2eb5064936d31619ea59398a3959daccbbc71f7ef292c45f'
spec = importlib.util.spec_from_file_location('skin_builder', Path(__file__).with_name('build-inventory-skins.py'))
builder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(builder)
stock_transform = builder.transform


def replace_once(source, old, new):
    if source.count(old) != 1:
        raise ValueError('Unrecognized source anchor: ' + old[:100])
    return source.replace(old, new, 1)


def transform(source, catalog):
    # Component_Library defines this class before InventoryWindow is loaded into the
    # same application domain. Upgrade its stock implementation as well as the v2 copy.
    if 'SKIN_CATEGORY_BY_ITEM' not in source:
        source = stock_transform(source, catalog)
    source = source.replace('   import ui.datasource.DataSourceConnection;',
                            '   import flash.utils.getDefinitionByName;')
    source = source.replace('m_skinSource:DataSourceConnection', 'm_skinSource:Object')
    source = replace_once(source, 'this.m_skinSource = DataSourceConnection.GetInstance(',
        'var sourceClass:Class = getDefinitionByName("ui.datasource.DataSourceConnection") as Class;\n'
        + '            this.m_skinSource = sourceClass.GetInstance(')
    source = builder.method(source, 'setItem', '''
      public function setItem(param1:IItemData, param2:Boolean = false) : void
      {
         this.stopSkinChoices();
         this.m_item = param1;
         this.m_skinCategory = this.ownedWeaponCategory(param1);
         if(this.m_item != null)
         {
            this.logInventoryContext();
            UIBindingItem.ItemUseOptionDataSourceUpdate(this.m_item.itemDefinitionId,this.m_item.containerOwnerGuid);
            stage.addEventListener(MouseEvent.MOUSE_DOWN,this.onMouseDownClick,false,0,true);
         }
         else
         {
            stage.removeEventListener(MouseEvent.MOUSE_DOWN,this.onMouseDownClick);
            if(!param2)
            {
               dispatchEvent(new Event(Event.CLOSE));
            }
         }
         invalidate();
      }
''')
    source = source.replace('UIBindingSystem.DispatchWallOfData("InventorySkin","choices category="',
                            'trace("InventoryActions v5 choices category="')
    options = (ROOT / 'src/Cranberry.Zone/Inventory/ItemUseOptions.g.cs').read_text(encoding='utf-8')
    hoodies = {item: True for item, values in re.findall(r'\[(\d+)u\] = \[([^\]]+)\]', options)
               if {'96u', '97u'}.issubset(set(values.split(', ')))}
    if len(hoodies) < 20:
        raise ValueError('Missing August hoodie option groups')
    source = replace_once(source, '      private var m_skinCategory:int = 0;',
        '      private static const HOODIE_ITEMS:Object = ' + json.dumps(hoodies, separators=(',', ':'))
        + ';\n\n      private var m_skinCategory:int = 0;')
    source = builder.method(source, 'ownedWeaponCategory', '''
      private function ownedWeaponCategory(item:IItemData) : int
      {
         if(item == null)
         {
            return 0;
         }
         var carried:Array = this.currentLoadoutRows();
         var index:int = 0;
         while(index < carried.length)
         {
            var row:Object = carried[index];
            if(String(row.ItemGuid) == item.guid)
            {
               var slot:int = int(row.ContainerSlotId);
               return slot == 1 || slot == 2 || slot == 4
                  ? int(SKIN_CATEGORY_BY_ITEM[String(row.ItemId)]) : 0;
            }
            index++;
         }
         return 0;
      }
''')
    source = builder.method(source, 'draw', '''
      override protected function draw() : void
      {
         super.draw();
         if(this.m_skinMode)
         {
            this.refreshSkinChoices();
            return;
         }
         this.m_skinCategory = this.ownedWeaponCategory(this.m_item);
         var rows:Array = uiDBManager.query(InventoryQueryStrings.getitemUseOptions()) || [];
         var actions:Array = [];
         var index:int = 0;
         while(index < rows.length)
         {
            var action:ItemActionData = new ItemActionData(rows[index]);
            if(action.id != 88 && action.id != 96 && action.id != 97)
            {
               actions.push(action);
            }
            index++;
         }
         if(this.m_skinCategory > 0)
         {
            actions.push(new ItemActionData({Id:88,Name:"Skin",IconId:151}));
         }
         var hood:int = this.hoodieContext(this.m_item);
         if(hood == 1)
         {
            actions.push(new ItemActionData({Id:96,Name:"Hood up",IconId:131}));
         }
         if(hood == 2)
         {
            actions.push(new ItemActionData({Id:97,Name:"Hood down",IconId:131}));
         }
         this.showActions(actions,true);
      }
''')
    source = replace_once(source,
        '                  UIBindingItem.SetSkinItemByItemIdWithCachedData(int(choice.accountId));',
        '                  UIBindingItem.RequestUseItem(this.m_item.guid,88,UIBindingPlayer.GetPlayerGuid());\n'
        + '                  UIBindingItem.SetSkinItemByItemIdWithCachedData(int(choice.accountId));')
    source = replace_once(source, '         ActionManager.getInstance().doAction(this.m_item,action);', '''         if(action.id == 88)
         {
            return;
         }
         if(action.id == 96 || action.id == 97)
         {
            var hood:int = this.hoodieContext(this.m_item);
            if(hood == 0 || (action.id == 96 && hood != 1) || (action.id == 97 && hood != 2))
            {
               this.setItem(null);
               return;
            }
         }
         ActionManager.getInstance().doAction(this.m_item,action);''')
    extra = '''
      private function hoodieContext(item:IItemData) : int
      {
         if(item == null)
         {
            return 0;
         }
         var rows:Array = this.currentLoadoutRows();
         var chest:Object = null;
         var hasHead:Boolean = false;
         var index:int = 0;
         while(index < rows.length)
         {
            var row:Object = rows[index];
            var slot:int = int(row.ContainerSlotId);
            hasHead = hasHead || slot == 11;
            if(slot == 10 && String(row.ItemGuid) == item.guid)
            {
               chest = row;
            }
            index++;
         }
         if(chest == null || hasHead || !HOODIE_ITEMS[String(chest.ItemId)])
         {
            return 0;
         }
         // The same authoritative state that chooses the worn mesh. Never infer the
         // posture from August's static native use-option row (it offers Down at spawn).
         var state:String = String(UIBindingSystem.GetStringHashValue("Cranberry.Inventory.Hood",""));
         return state == item.guid + ":1" ? 2 : 1;
      }

      private function currentLoadoutRows() : Array
      {
         // Use the same query and 64-bit GUID projection as the displayed loadout.
         return uiDBManager.query(InventoryQueryStrings.getLoadoutItemsQuery()) || [];
      }

      private function logInventoryContext() : void
      {
         var rows:Array = this.currentLoadoutRows();
         var detail:String = "v5 item=" + this.m_item.itemDefinitionId + " guid=" + this.m_item.guid
            + " slot=" + this.m_item.containerSlotId + " container=" + this.m_item.containerGuid
            + " category=" + this.m_skinCategory + " loadout=";
         var index:int = 0;
         while(index < rows.length && index < 20)
         {
            var row:Object = rows[index];
            detail += String(row.ItemGuid) + ":" + row.ContainerSlotId + ":" + row.ItemId + ";";
            index++;
         }
         if(UIBindingSystem.Print != null)
         {
            UIBindingSystem.Print("InventoryActions " + detail);
         }
         if(UIBindingSystem.DispatchWallOfData != null)
         {
            UIBindingSystem.DispatchWallOfData("InventoryActions",detail);
         }
      }
'''
    at = source.rfind('   }')
    return source[:at] + extra + '\n' + source[at:]


if __name__ == '__main__':
    builder.SOURCE_SHA = SOURCE_SHA256
    builder.transform = transform
    builder.main()
