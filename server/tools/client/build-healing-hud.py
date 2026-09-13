#!/usr/bin/env python3
"""Use the August HUD's authored bandage and first-aid frames while server healing is active."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess

SOURCE_SHA = 'd41966ec348dc120709430230a4c454af64413daa2e397bee45988f381af26e2'
CLASS = 'views.hudplayerresources.EffectView'

def transform(source):
    assert 'Cranberry.Healing' not in source
    source = source.replace('   import flash.display.MovieClip;',
        '   import flash.display.MovieClip;\n   import flash.events.Event;\n   import flash.utils.getTimer;\n   import ui.bindings.UIBindingSystem;')
    source = source.replace('      private var m_type:String = "";',
        '      private var m_type:String = "";\n      private var m_lastHealingCheck:int = -250;\n      private var m_lastHealingValue:String = "";')
    source = source.replace('         this.m_type = param1;',
        '         this.m_type = param1;\n         if(param1 == HEALING) { this.addEventListener(Event.ENTER_FRAME,this.pollHealing,false,0,true); }')
    source = source.replace('      public function cleanup() : void\n      {',
        '      public function cleanup() : void\n      {\n         this.removeEventListener(Event.ENTER_FRAME,this.pollHealing);')
    needle = '      private function updateHealing() : void\n      {'
    assert source.count(needle) == 1
    source = source.replace(needle, needle + '''
         var active:String = String(UIBindingSystem.GetStringHashValue("Cranberry.Healing","native"));
         if(active != "native")
         {
            this.gotoAndStop(active == "120581" ? 3 : (active == "120583" ? 2 : 1));
            return;
         }
''')
    pos = source.rfind('   }')
    return source[:pos] + '''
      private function pollHealing(event:Event) : void
      {
         var now:int = getTimer();
         if(now - this.m_lastHealingCheck < 250) { return; }
         this.m_lastHealingCheck = now;
         var active:String = String(UIBindingSystem.GetStringHashValue("Cranberry.Healing","native"));
         if(active != this.m_lastHealingValue)
         {
            this.m_lastHealingValue = active;
            this.updateHealing();
         }
      }
''' + source[pos:]

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('asset',type=Path);p.add_argument('--ffdec',type=Path,required=True);p.add_argument('--out',type=Path,required=True)
    args=p.parse_args(); raw=args.asset.read_bytes()
    assert hashlib.sha256(raw).hexdigest()==SOURCE_SHA,'Unknown HUD input; preserve existing changes'
    args.out.mkdir(parents=True,exist_ok=True)
    java=['java','-jar',str(args.ffdec.resolve())]
    subprocess.run(java+['-selectclass',CLASS,'-export','script',str(args.out/'source'),str(args.asset)],check=True)
    script=args.out/'source/scripts/views/hudplayerresources/EffectView.as'
    script.write_text(transform(script.read_text(encoding='utf-8')),encoding='utf-8')
    compiled=args.out/'compiled.gfx'
    subprocess.run(java+['-onerror','abort','-importScript',str(args.asset),str(compiled),str(args.out/'source/scripts')],check=True)
    spec=importlib.util.spec_from_file_location('preserve',Path(__file__).with_name('build-helmet-click.py'))
    preserve=importlib.util.module_from_spec(spec);spec.loader.exec_module(preserve)
    built=preserve.preserve_tags(raw,compiled.read_bytes());target=args.out/args.asset.name;target.write_bytes(built)
    subprocess.run(java+['-selectclass',CLASS,'-export','script',str(args.out/'verify'),str(target)],check=True)
    print(json.dumps({'asset':str(target),'sha256':hashlib.sha256(built).hexdigest(),'sourceSha256':SOURCE_SHA}))

if __name__=='__main__':main()
