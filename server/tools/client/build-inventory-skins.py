#!/usr/bin/env python3
"""Add the owned-weapon Skin submenu to the actual installed InventoryWindow.

Uses the existing native skin datasource and cached-selection binding. Category mappings
come from AugustSkinCatalog.Weapons; no ownership or packet format is invented. Never installs.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]
SOURCE_SHA = '88317f538ab870f5e98aca9f09a3c664437345a502a401bd822fab7af6e90c1d'
ASSET_NAME = 'InventoryWindow.gfx'


def method(source, name, replacement):
    match = re.search(r'      (?:override )?(?:protected|private|public) function ' + name + r'\(', source)
    if not match:
        raise ValueError('Missing original method ' + name)
    start = source.index('{', match.end())
    end, depth = start + 1, 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[:match.start()] + replacement.strip('\n') + source[end:]


def transform(source, catalog):
    weapons = catalog.split('IReadOnlyList<AugustSkinCatalogEntry> Weapons =', 1)[1].split('];', 1)[0]
    rows = [tuple(map(int, row)) for row in re.findall(r'new\((\d+), (\d+), (\d+),', weapons)]
    if len(rows) < 50:
        raise ValueError('Missing weapon catalogue rows')
    item_map, account_map = {}, {}
    for category, reward, account in rows:
        item_map[str(category)] = category
        item_map[str(reward)] = category
        account_map[str(account)] = category
    # August gameplay AR-15 2425 shares the native weapon definition (PARAM1=6),
    # name and equipment slots of category 10 but is not itself a skin reward.
    item_map['2425'] = 10
    imports = '   import ui.bindings.UIBindingPlayer;\n   import ui.bindings.UIBindingSystem;\n   import ui.datasource.DataSourceConnection;\n'
    source = source.replace('   import ui.bindings.UIBindingItem;\n', '   import ui.bindings.UIBindingItem;\n' + imports, 1)
    fields = '\n'.join([
        '      private static const SKIN_CATEGORY_BY_ITEM:Object = ' + json.dumps(item_map, separators=(',', ':')) + ';',
        '      private static const SKIN_CATEGORY_BY_ACCOUNT:Object = ' + json.dumps(account_map, separators=(',', ':')) + ';',
        '      private var m_skinCategory:int = 0;',
        '      private var m_skinMode:Boolean = false;',
        '      private var m_skinSource:DataSourceConnection;',
        '      private var m_regularWidth:Number = 0;',
    ])
    source = source.replace('      private var m_adjustX:Number = 0;', fields + '\n\n      private var m_adjustX:Number = 0;', 1)
    source = method(source, 'draw', '''
      override protected function draw() : void
      {
         super.draw();
         if(this.m_skinMode)
         {
            this.refreshSkinChoices();
            return;
         }
         var rows:Array = uiDBManager.query(InventoryQueryStrings.getitemUseOptions());
         var actions:Array = [];
         var hasSkin:Boolean = false;
         var index:int = 0;
         while(index < rows.length)
         {
            var action:ItemActionData = new ItemActionData(rows[index]);
            actions.push(action);
            hasSkin = hasSkin || action.id == 88;
            index++;
         }
         if(this.m_skinCategory > 0 && !hasSkin)
         {
            actions.push(new ItemActionData({Id:88,Name:"Skin",IconId:151}));
         }
         this.showActions(actions,true);
      }
''')
    source = method(source, 'setItem', '''
      public function setItem(param1:IItemData, param2:Boolean = false) : void
      {
         this.stopSkinChoices();
         this.m_item = param1;
         this.m_skinCategory = this.ownedWeaponCategory(param1);
         if(this.m_item != null)
         {
            UIBindingSystem.DispatchWallOfData("InventorySkin","open item=" + this.m_item.itemDefinitionId
               + " guid=" + this.m_item.guid + " owner=" + this.m_item.containerOwnerGuid
               + " player=" + UIBindingPlayer.GetPlayerGuid() + " weapon=" + this.m_item.itemIsWeapon
               + " category=" + this.m_skinCategory);
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
    source = method(source, 'handleListItemClick', '''
      protected function handleListItemClick(param1:ListEvent) : void
      {
         var choice:Object = this.m_list.dataProvider.requestItemAt(param1.index);
         if(this.m_skinMode)
         {
            if(int(choice.accountId) == -1)
            {
               this.stopSkinChoices();
               invalidate();
               return;
            }
            if(int(choice.accountId) > 0 && this.ownedWeaponCategory(this.m_item) == this.m_skinCategory
               && int(SKIN_CATEGORY_BY_ACCOUNT[String(choice.accountId)]) == this.m_skinCategory)
            {
               var owned:Array = uiDBManager.query("SELECT ItemCount FROM AccountInventory WHERE ItemId == " + int(choice.accountId));
               if(owned.length > 0 && int(owned[0].ItemCount) > 0)
               {
                  UIBindingItem.SelectEditorSkinItemCollectionId(2);
                  UIBindingItem.SelectEditorSkinTargetPrototypeItemId(this.m_skinCategory);
                  UIBindingItem.SetSkinItemByItemIdWithCachedData(int(choice.accountId));
                  this.setItem(null);
               }
            }
            return;
         }
         var action:ItemActionData = choice as ItemActionData;
         if(action.id == 88 && this.m_skinCategory > 0)
         {
            this.m_skinMode = true;
            UIBindingItem.SelectEditorSkinItemCollectionId(2);
            UIBindingItem.SelectEditorSkinItemSlotType(2);
            UIBindingItem.SelectEditorSkinTargetPrototypeItemId(this.m_skinCategory);
            this.m_skinSource = DataSourceConnection.GetInstance("Items.AvailableAccountSkinItemDataSource",this.refreshSkinChoices,this.refreshSkinChoices);
            this.refreshSkinChoices();
            UIBindingSystem.DispatchWallOfData("InventorySkin","choices category=" + this.m_skinCategory
               + " rows=" + this.m_skinSource.GetRowCount());
            return;
         }
         ActionManager.getInstance().doAction(this.m_item,action);
         this.setItem(null);
      }
''')
    source = source.replace('         this.m_list.removeEventListener(ListEvent.ITEM_CLICK,this.handleListItemClick);',
                            '         this.stopSkinChoices();\n         this.m_list.removeEventListener(ListEvent.ITEM_CLICK,this.handleListItemClick);', 1)
    extra = '''
      private function ownedWeaponCategory(item:IItemData) : int
      {
         if(item == null || !/^-?[0-9]+$/.test(item.guid))
         {
            return 0;
         }
         var carried:Array = uiDBManager.query("SELECT ItemId FROM PlayerInventory WHERE CAST(ItemGuid AS TEXT) == '" + item.guid + "'") || [];
         if(carried.length != 1)
         {
            return 0;
         }
         return int(SKIN_CATEGORY_BY_ITEM[String(carried[0].ItemId)]);
      }

      private function stopSkinChoices() : void
      {
         this.m_skinMode = false;
         if(this.m_skinSource != null)
         {
            this.m_skinSource.RemoveUpdateListener(this.refreshSkinChoices);
            this.m_skinSource.RemoveDataChangedListener(this.refreshSkinChoices);
            this.m_skinSource = null;
         }
         if(this.m_regularWidth > 0 && this.m_list != null)
         {
            this.m_list.width = this.m_regularWidth;
         }
      }

      private function refreshSkinChoices() : void
      {
         if(!this.m_skinMode || this.m_skinSource == null || this.m_item == null)
         {
            return;
         }
         var choices:Array = [];
         var seen:Object = {};
         var index:int = 0;
         while(index < this.m_skinSource.GetRowCount())
         {
            var accountId:int = int(this.m_skinSource.GetData(index,"Item Definition Id"));
            if(int(SKIN_CATEGORY_BY_ACCOUNT[String(accountId)]) == this.m_skinCategory
               && this.m_skinSource.GetData(index,"AccountItemIsOwned") == "1"
               && int(this.m_skinSource.GetData(index,"AccountItemCount")) > 0 && !seen[String(accountId)])
            {
               seen[String(accountId)] = true;
               choices.push({accountId:accountId,name:this.m_skinSource.GetData(index,"Name"),iconId:int(this.m_skinSource.GetData(index,"Icon ID"))});
            }
            index++;
         }
         choices.sortOn("name",Array.CASEINSENSITIVE);
         if(choices.length == 0)
         {
            choices.push({accountId:0,name:"No skins owned",iconId:0});
         }
         choices.unshift({accountId:-1,name:"Back",iconId:0});
         this.showActions(choices,false);
      }

      private function showActions(actions:Array, reposition:Boolean) : void
      {
         this.visible = this.m_item != null && actions.length > 0;
         if(!this.visible)
         {
            return;
         }
         if(this.m_regularWidth == 0)
         {
            this.m_regularWidth = this.m_list.width;
         }
         this.m_actions = actions;
         var visibleRows:int = Math.min(actions.length,14);
         this.m_list.width = this.m_skinMode ? Math.max(this.m_regularWidth,340) : this.m_regularWidth;
         this.m_list.height = visibleRows * 25;
         this.m_list.dataProvider = new DataProvider(actions);
         this.m_list.rowCount = visibleRows;
         this.m_list.validateNow();
         this.m_list.selectedIndex = -1;
         if(reposition)
         {
            var point:Point = parent.globalToLocal(new Point(stage.mouseX,stage.mouseY));
            this.x = point.x;
            this.y = point.y;
         }
         var corner:Point = parent.globalToLocal(new Point(stage.stageWidth,stage.stageHeight));
         this.x = Math.max(0,Math.min(this.x,corner.x - this.m_list.width));
         this.y = Math.max(0,Math.min(this.y,corner.y - this.m_list.height));
      }
'''
    at = source.rfind('   }')
    return source[:at] + extra + '\n' + source[at:]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('asset', type=Path)
    parser.add_argument('--ffdec', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    raw = args.asset.read_bytes()
    if hashlib.sha256(raw).hexdigest() != SOURCE_SHA:
        raise ValueError('Unrecognized source; preserve other installed InventoryWindow changes')
    out = args.out.resolve(); out.mkdir(parents=True, exist_ok=True)
    java = ['java', '-jar', str(args.ffdec.resolve())]
    cls = 'ui.controls.ItemActionMenu'
    subprocess.run(java + ['-selectclass', cls, '-export', 'script', str(out / 'source'), str(args.asset.resolve())], check=True)
    script = out / 'source/scripts/ui/controls/ItemActionMenu.as'
    catalog = ROOT / 'src/Cranberry.Zone/AugustSkinCatalog.g.cs'
    script.write_text(transform(script.read_text(encoding='utf-8'), catalog.read_text(encoding='utf-8')), encoding='utf-8')
    compiled = out / 'compiled.gfx'
    subprocess.run(java + ['-onerror', 'abort', '-importScript', str(args.asset.resolve()), str(compiled), str(out / 'source/scripts')], check=True)
    spec = importlib.util.spec_from_file_location('preserve', Path(__file__).with_name('build-helmet-click.py'))
    preserve = importlib.util.module_from_spec(spec); spec.loader.exec_module(preserve)
    built = preserve.preserve_tags(raw, compiled.read_bytes())
    target = out / ASSET_NAME; target.write_bytes(built)
    subprocess.run(java + ['-selectclass', cls, '-export', 'script', str(out / 'verify'), str(target)], check=True)
    print(json.dumps({'asset': str(target), 'sha256': hashlib.sha256(built).hexdigest(),
                      'sourceSha256': SOURCE_SHA, 'catalogSha256': hashlib.sha256(catalog.read_bytes()).hexdigest()}, indent=2))


if __name__ == '__main__':
    main()
