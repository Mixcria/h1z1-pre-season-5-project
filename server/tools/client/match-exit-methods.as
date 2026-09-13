      private function handleMatchSessionReady(event:GameEvent) : void
      {
         this.m_exitRequested = false;
         this.m_nativeExitStarted = false;
      }

      private function requestMatchAction(action:String) : void
      {
         if(this.m_exitRequested) { return; }
         this.m_exitRequested = true;
         UIBindingSystem.DispatchWallOfData("CRANBERRY_MATCH_ACTION_V1",action);
      }

      private function handleNativeMatchExit(event:GameEvent) : void
      {
         if(this.m_nativeExitStarted) { return; }
         this.m_nativeExitStarted = true;
         // Leave the incoming packet's UI event stack before native teardown.
         setTimeout(this.finishNativeMatchExit,0);
      }

      private function finishNativeMatchExit() : void
      {
         UIBindingSound.PlayUiSound(UIBindingSound.UI_MATCH_END_PROGRESS_BAR_STOP);
         this.cleanUpMatchData();
         this.closeAllWindows();
         UIBindingSystem.Logout();
      }

      private function installResultButtons(button:Button) : void
      {
         if(this.m_resultRows[button] || !button.parent) { return; }
         var row:Sprite = new Sprite();
         row.x = button.x;
         row.y = button.y;
         var width:Number = Math.max(100,(button.width - 12) / 2);
         var height:Number = Math.max(32,button.height);
         row.addChild(this.makeResultButton("PLAY AGAIN","play",width,height,0xB51F29));
         var menu:Sprite = this.makeResultButton("MAIN MENU","menu",width,height,0x252525);
         menu.x = width + 12;
         row.addChild(menu);
         button.parent.addChild(row);
         this.m_resultRows[button] = row;
         button.visible = false;
         button.addEventListener(Event.REMOVED_FROM_STAGE,this.removeResultButtons,false,0,true);
      }

      private function makeResultButton(label:String, action:String, width:Number, height:Number, color:uint) : Sprite
      {
         var button:Sprite = new Sprite();
         button.name = action;
         button.buttonMode = true;
         button.mouseChildren = false;
         button.tabEnabled = true;
         button.graphics.beginFill(color,1);
         button.graphics.drawRect(0,0,width,height);
         button.graphics.endFill();
         var text:TextField = new TextField();
         text.defaultTextFormat = new TextFormat("Arial",Math.min(22,height * 0.38),0xFFFFFF,true,null,null,null,null,"center");
         text.width = width;
         text.height = height;
         text.y = Math.max(0,(height - 28) / 2);
         text.selectable = false;
         text.text = label;
         button.addChild(text);
         button.addEventListener(MouseEvent.CLICK,this.handleResultClick,false,0,true);
         button.addEventListener(KeyboardEvent.KEY_UP,this.handleResultKey,false,0,true);
         return button;
      }

      private function handleResultClick(event:MouseEvent) : void
      {
         this.requestMatchAction(event.currentTarget.name);
      }

      private function handleResultKey(event:KeyboardEvent) : void
      {
         if(event.keyCode == 13 || event.keyCode == 32)
         {
            this.requestMatchAction(event.currentTarget.name);
         }
      }

      private function removeResultButtons(event:Event) : void
      {
         var button:Button = event.currentTarget as Button;
         var row:Sprite = this.m_resultRows[button];
         if(row && row.parent) { row.parent.removeChild(row); }
         delete this.m_resultRows[button];
         button.removeEventListener(Event.REMOVED_FROM_STAGE,this.removeResultButtons);
      }
