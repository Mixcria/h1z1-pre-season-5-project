      public static function OverlayActive() : Boolean
      {
         return m_overlay != null && m_overlay.m_overlayRoot != null && m_overlay.m_overlayRoot.visible;
      }

      private function overlayInitialize() : void
      {
         m_overlay = this;
         this.m_overlayTimer = new Timer(250);
         this.m_overlayTimer.addEventListener(TimerEvent.TIMER,this.overlayTick,false,0,true);
         this.m_overlayTimer.start();
         this.m_stage.addEventListener(KeyboardEvent.KEY_DOWN,this.overlayKey,true,100,true);
         this.m_stage.addEventListener(Event.RESIZE,this.overlayResize,false,0,true);
         this.m_stage.addEventListener(WidgetEvent.CLOSE_ALL,this.overlayCloseAll,false,100,true);
      }

      private function overlayDeinitialize() : void
      {
         this.overlayClose();
         this.m_overlayTimer.stop();
         this.m_overlayTimer.removeEventListener(TimerEvent.TIMER,this.overlayTick);
         this.m_stage.removeEventListener(KeyboardEvent.KEY_DOWN,this.overlayKey,true);
         this.m_stage.removeEventListener(Event.RESIZE,this.overlayResize);
         this.m_stage.removeEventListener(WidgetEvent.CLOSE_ALL,this.overlayCloseAll);
         if(this.m_overlayRoot != null && this.m_overlayRoot.parent != null) this.m_overlayRoot.parent.removeChild(this.m_overlayRoot);
         this.m_overlayRoot = null;
         m_overlay = null;
      }

      private function overlayCloseAll(event:WidgetEvent) : void
      {
         this.overlayClose();
      }

      private function overlayLabel(parent:DisplayObjectContainer,text:String,x:Number,y:Number,w:Number,h:Number,size:int = 18,color:uint = 15198183) : TextField
      {
         var field:TextField = new TextField();
         field.defaultTextFormat = new TextFormat("$MainFontRegular",size,color);
         field.text = text;
         field.x = x; field.y = y; field.width = w; field.height = h;
         field.selectable = false; field.mouseEnabled = false;
         parent.addChild(field);
         return field;
      }

      private function overlayButton(parent:DisplayObjectContainer,text:String,x:Number,y:Number,w:Number,callback:Function) : Sprite
      {
         var button:Sprite = new Sprite();
         button.x = x; button.y = y;
         button.graphics.beginFill(4608089); button.graphics.drawRect(0,0,w,38); button.graphics.endFill();
         this.overlayLabel(button,text,12,7,w - 20,28,17);
         button.buttonMode = true;
         button.addEventListener(MouseEvent.CLICK,function(event:MouseEvent):void { callback(); event.stopPropagation(); });
         parent.addChild(button);
         return button;
      }

      private function overlayBuild() : void
      {
         this.m_overlayRoot = new Sprite();
         this.m_overlayRoot.name = "CranberryFriendsOverlay";
         this.m_overlayPanel = new Sprite();
         this.m_overlayPanel.graphics.beginFill(1514267); this.m_overlayPanel.graphics.drawRect(0,0,1040,660); this.m_overlayPanel.graphics.endFill();
         this.m_overlayPanel.graphics.beginFill(2566700); this.m_overlayPanel.graphics.drawRect(20,100,290,530); this.m_overlayPanel.graphics.endFill();
         this.m_overlayRoot.addChild(this.m_overlayPanel);
         this.overlayLabel(this.m_overlayPanel,"CRANBERRY  /  FRIENDS",26,20,700,46,29);
         this.overlayLabel(this.m_overlayPanel,"Shift+Tab or Esc to return to the game",28,66,700,26,16,10528163);
         this.overlayButton(this.m_overlayPanel,"CLOSE",922,24,90,this.overlayClose);
         this.m_overlayList = new Sprite(); this.m_overlayList.x = 28; this.m_overlayList.y = 108;
         this.m_overlayPanel.addChild(this.m_overlayList);
         this.m_overlayList.addEventListener(MouseEvent.MOUSE_WHEEL,this.overlayWheel,false,0,true);
         this.m_overlayPeer = this.overlayLabel(this.m_overlayPanel,"Select a friend",336,110,660,40,25);
         this.m_overlayHistory = this.overlayLabel(this.m_overlayPanel,"Your private conversations appear here.",336,164,674,356,19);
         this.m_overlayHistory.multiline = true; this.m_overlayHistory.wordWrap = true;
         this.m_overlayHistory.selectable = true; this.m_overlayHistory.mouseEnabled = true;
         this.m_overlayHistory.addEventListener(MouseEvent.MOUSE_WHEEL,function(event:MouseEvent):void
         { m_overlay.m_overlayHistory.scrollV -= event.delta * 3; event.stopPropagation(); });
         this.m_overlayInput = this.overlayLabel(this.m_overlayPanel,"",342,544,548,39,20);
         this.m_overlayInput.type = TextFieldType.INPUT; this.m_overlayInput.maxChars = 1000;
         this.m_overlayInput.background = true; this.m_overlayInput.backgroundColor = 3093561;
         this.m_overlayInput.mouseEnabled = true; this.m_overlayInput.selectable = true;
         this.overlayButton(this.m_overlayPanel,"SEND",910,544,100,this.overlaySendMessage);
         this.m_overlayStatus = this.overlayLabel(this.m_overlayPanel,"Add friends and change your picture in the launcher.",338,600,670,44,16,10528163);
         this.m_overlayStatus.wordWrap = true;
         this.m_stage.addChild(this.m_overlayRoot);
         this.m_overlayRoot.visible = false;
         this.overlayResize();
      }

      private function overlayResize(event:Event = null) : void
      {
         if(this.m_overlayRoot == null) return;
         this.m_overlayRoot.graphics.clear(); this.m_overlayRoot.graphics.beginFill(0,0.65);
         this.m_overlayRoot.graphics.drawRect(0,0,this.m_stage.stageWidth,this.m_stage.stageHeight); this.m_overlayRoot.graphics.endFill();
         var scale:Number = Math.min(1.4,Math.min((this.m_stage.stageWidth - 40) / 1040,(this.m_stage.stageHeight - 40) / 660));
         this.m_overlayPanel.scaleX = this.m_overlayPanel.scaleY = Math.max(0.35,scale);
         this.m_overlayPanel.x = (this.m_stage.stageWidth - 1040 * scale) / 2;
         this.m_overlayPanel.y = (this.m_stage.stageHeight - 660 * scale) / 2;
      }

      private function overlayToggle() : void
      {
         if(OverlayActive()) { this.overlayClose(); return; }
         if(this.m_overlayRoot == null) this.overlayBuild();
         this.m_overlayFlags = uint(UIBindingInput.GetInputFlags());
         this.m_overlayMouseHidden = Boolean(UIBindingSystem.GetMouseHidden());
         this.m_overlayMovement = Boolean(UIBindingKeyboard.GetMovementEnabled());
         this.m_overlayKeyboard = Boolean(UIBindingKeyboard.GetKeyboardEnabled());
         this.m_overlayFocus = this.m_stage.focus;
         this.m_stage.setChildIndex(this.m_overlayRoot,this.m_stage.numChildren - 1);
         this.m_overlayRoot.visible = true;
         UIBindingInput.SetInputFlags(this.m_overlayFlags | UIBindingInput.INPUT_MOUSE | UIBindingInput.INPUT_KEYBOARD);
         UIBindingSystem.SetMouseHidden(false);
         UIBindingKeyboard.SetMovementEnabled(false); UIBindingKeyboard.SetKeyboardEnabled(false);
         this.m_stage.focus = this.m_overlayInput;
         this.overlayQueue("state");
         if(this.m_overlayFriend != "") this.overlayQueue("history|" + this.m_overlayFriend);
         this.m_overlayNextPoll = getTimer() + 2000;
      }

      private function overlayClose() : void
      {
         if(!OverlayActive()) return;
         this.m_overlayDrafts[this.m_overlayFriend] = this.m_overlayInput.text;
         this.m_overlayRoot.visible = false;
         this.m_stage.focus = this.m_overlayFocus != null && this.m_overlayFocus.stage != null ? this.m_overlayFocus : null;
         // The current UI releases overlay capture after checking native input ownership.
         if(!this.m_stage.dispatchEvent(new flash.events.DataEvent("cranberryInputRefresh",false,true,String(this.m_overlayFlags))))
         {
            return;
         }
         // Outside InGame no recovery listener exists: retain the original menu restoration.
         UIBindingInput.SetInputFlags(this.m_overlayFlags);
         UIBindingSystem.SetMouseHidden(this.m_overlayMouseHidden);
         UIBindingKeyboard.SetMovementEnabled(this.m_overlayMovement); UIBindingKeyboard.SetKeyboardEnabled(this.m_overlayKeyboard);
      }

      private function overlayKey(event:KeyboardEvent) : void
      {
         if(!OverlayActive()) return;
         if(event.keyCode == Keyboard.ESCAPE) { this.overlayClose(); event.preventDefault(); event.stopImmediatePropagation(); }
         else if(event.keyCode == Keyboard.ENTER || event.keyCode == Keyboard.NUMPAD_ENTER)
         { this.overlaySendMessage(); event.preventDefault(); event.stopImmediatePropagation(); }
      }

      private function overlayQueue(action:String) : void
      {
         if(this.m_overlayJobs.indexOf(action) < 0 && this.m_overlayWaiting != action && this.m_overlayJobs.length < 64)
            this.m_overlayJobs.push(action);
      }

      private function overlayTick(event:TimerEvent) : void
      {
         UILobbyFriendsManager.LocalAvatars();
         if(OverlayActive() && getTimer() >= this.m_overlayNextPoll)
         {
            this.overlayQueue("state");
            if(this.m_overlayFriend != "") this.overlayQueue("history|" + this.m_overlayFriend);
            this.m_overlayNextPoll = getTimer() + 2000;
         }
         if(this.m_overlayWaiting != "" && getTimer() - this.m_overlaySentAt > 12000)
         {
            this.m_overlayWaiting = "";
            if(this.m_overlayStatus != null) this.m_overlayStatus.text = "Connection delayed. Press Send to retry your message.";
         }
         if(this.m_overlayWaiting == "" && this.m_overlayJobs.length > 0)
         {
            this.m_overlayWaiting = String(this.m_overlayJobs.shift());
            this.m_overlaySentAt = getTimer();
            UIBindingSystem.DispatchWallOfData("CRANBERRY_OVERLAY_V1",this.m_overlayWaiting);
         }
      }

      private function overlayWheel(event:MouseEvent) : void
      {
         this.m_overlayScroll = Math.max(0,Math.min(Math.max(0,this.m_overlayFriends.length - 7),this.m_overlayScroll - event.delta));
         this.overlayFriends(); event.stopPropagation();
      }

      private function overlaySelect(friend:Object) : void
      {
         this.m_overlayDrafts[this.m_overlayFriend] = this.m_overlayInput.text;
         this.m_overlayFriend = friend.id;
         this.m_overlayInput.text = this.m_overlayDrafts[friend.id] == null ? "" : String(this.m_overlayDrafts[friend.id]);
         this.m_overlayPeer.text = friend.name + "  /  " + friend.status;
         this.m_overlayHistory.text = "Loading conversation…";
         this.m_overlayHistoryKey = "";
         this.m_overlayStatus.text = "Messages are saved. Offline friends can read them when they return.";
         this.overlayQueue("history|" + friend.id);
         this.overlayFriends(); this.m_stage.focus = this.m_overlayInput;
      }

      private function overlayFriendButton(friend:Object,y:Number) : void
      {
         var row:Sprite = new Sprite(); row.y = y;
         row.graphics.beginFill(friend.id == this.m_overlayFriend ? 4147026 : 2566700);
         row.graphics.drawRect(0,0,274,66); row.graphics.endFill();
         var picture:Sprite = new Sprite(); picture.x = 9; picture.y = 10;
         row.addChild(picture); OverlayAvatar(picture,friend.id,friend.avatar,42,42);
         this.overlayLabel(row,friend.name,60,8,210,27,19);
         this.overlayLabel(row,friend.unread > 0 ? String(friend.unread) + " unread  /  " + friend.status : friend.status,60,36,210,24,15,
            friend.status == "Offline" ? 10528163 : 10207884);
         row.buttonMode = true;
         row.addEventListener(MouseEvent.CLICK,function(event:MouseEvent):void { m_overlay.overlaySelect(friend); event.stopPropagation(); });
         this.m_overlayList.addChild(row);
      }

      private function overlayFriends() : void
      {
         if(this.m_overlayList == null) return;
         while(this.m_overlayList.numChildren > 0) this.m_overlayList.removeChildAt(0);
         var y:Number = 0;
         for each(var friend:Object in this.m_overlayFriends.slice(this.m_overlayScroll,this.m_overlayScroll + 7))
         { this.overlayFriendButton(friend,y); y += 70; }
         if(this.m_overlayFriends.length == 0) this.overlayLabel(this.m_overlayList,"No friends yet.\nAdd friends in the launcher.",10,20,255,100,18);
         if(this.m_overlayFriends.length > 7) this.overlayLabel(this.m_overlayList,"Scroll to see more friends",10,494,255,26,15,10528163);
      }

      private function overlaySendMessage() : void
      {
         if(this.m_overlayFriend == "" || this.m_overlayInput.text.replace(/\s/g,"") == "") return;
         if(this.m_overlayPending == null || this.m_overlayPending.target != this.m_overlayFriend || this.m_overlayPending.text != this.m_overlayInput.text)
         {
            var id:String = "";
            for(var index:int = 0; index < 32; index++) id += "0123456789abcdef".charAt(int(Math.random() * 16));
            this.m_overlayPending = {"id":id,"target":this.m_overlayFriend,"text":this.m_overlayInput.text};
         }
         this.overlayQueue("send|" + this.m_overlayPending.target + "|" + this.m_overlayPending.id + "|" + encodeURIComponent(this.m_overlayPending.text));
         this.m_overlayStatus.text = "Sending…";
      }

      public static function OverlayAvatar(host:DisplayObjectContainer,account:String,version:String,w:Number = 48,h:Number = 48) : Boolean
      {
         if(m_overlay == null || host == null) return false;
         var picture:Sprite = host.getChildByName("cranberryAvatar") as Sprite;
         var key:String = account + "/" + version;
         var pixels:String = m_overlay.m_overlayAvatars[key] == null ? "" : String(m_overlay.m_overlayAvatars[key]);
         if(version != "" && pixels == "") m_overlay.overlayQueue("avatar|" + account);
         if(picture != null && m_overlay.m_overlayPainted[picture] == key)
            return pixels.length == 13824;
         if(picture != null) host.removeChild(picture);
         picture = new Sprite(); picture.name = "cranberryAvatar"; picture.mouseEnabled = false; picture.mouseChildren = false;
         if(pixels.length == 13824)
         {
            var offset:int = 0;
            for(var y:int = 0; y < 48; y++)
            {
               for(var x:int = 0; x < 48; x++)
               {
                  var color:uint = uint("0x" + pixels.substr(offset,6)); offset += 6;
                  var run:int = 1;
                  while(x + run < 48 && pixels.substr(offset,6) == pixels.substr(offset - 6,6)) { run++; offset += 6; }
                  picture.graphics.beginFill(color); picture.graphics.drawRect(x,y,run,1); picture.graphics.endFill(); x += run - 1;
               }
            }
         }
         else
         {
            picture.graphics.beginFill(4608089); picture.graphics.drawRect(0,0,48,48); picture.graphics.endFill();
            picture.graphics.beginFill(10396843); picture.graphics.drawCircle(24,17,8); picture.graphics.drawRoundRect(10,29,28,18,12); picture.graphics.endFill();
         }
         picture.scaleX = w / 48; picture.scaleY = h / 48; picture.cacheAsBitmap = true;
         if(pixels.length == 13824 || version == "")
         { m_overlay.m_overlayPainted[picture] = key; }
         host.addChild(picture);
         return pixels.length == 13824;
      }

      private function overlayReceive(text:String) : Boolean
      {
         if(text.indexOf("@cranberry/overlay/1;") != 0) return false;
         if(text.length > 524288) return true;
         var rows:Array = text.substr("@cranberry/overlay/1;".length).split(";");
         var header:Array = String(rows[0]).split("|");
         if(header[0] == "T" && header.length == 2)
         {
            if(header[1] != this.m_overlayToggleId) { this.m_overlayToggleId = header[1]; this.overlayToggle(); }
            return true;
         }
         this.m_overlayWaiting = "";
         var fields:Array;
         var row:String;
         if(header[0] == "S" && header.length == 4)
         {
            this.m_overlaySelf = decodeURIComponent(header[1]);
            var friends:Array = [];
            for each(row in rows.slice(1))
            {
               fields = row.split("|");
               if(fields[0] == "F" && fields.length == 6) friends.push({"id":decodeURIComponent(fields[1]),"name":decodeURIComponent(fields[2]),
                  "status":decodeURIComponent(fields[3]),"avatar":fields[4],"unread":int(fields[5])});
            }
            this.m_overlayFriends = friends;
            var found:Boolean = this.m_overlayFriend == "";
            for each(var friend:Object in friends) if(friend.id == this.m_overlayFriend) { found = true; if(this.m_overlayPeer != null) this.m_overlayPeer.text = friend.name + "  /  " + friend.status; }
            if(!found && this.m_overlayInput != null)
            { this.m_overlayFriend = ""; this.m_overlayInput.text = ""; this.m_overlayHistory.text = "This friend is no longer on your list."; this.m_overlayPeer.text = "Select a friend"; }
            if(this.m_overlayStateKey != text) { this.m_overlayStateKey = text; this.overlayFriends(); }
         }
         else if(header[0] == "H" && header.length == 3 && decodeURIComponent(header[1]) == this.m_overlayFriend && this.m_overlayHistory != null)
         {
            var output:String = "";
            var incoming:String = "0";
            for each(row in rows.slice(1))
            {
               fields = row.split("|");
               if(fields[0] != "M" || fields.length != 6) continue;
               var own:Boolean = decodeURIComponent(fields[2]) == this.m_overlaySelf;
               var date:Date = new Date(Number(fields[3]) * 1000);
               output += (own ? "You" : this.m_overlayPeer.text.split("  /  ")[0]) + "  ·  " + date.toLocaleString() + "\n" + decodeURIComponent(fields[4]) + "\n\n";
               if(!own) incoming = fields[1];
            }
            if(this.m_overlayHistoryKey != output)
            {
               var atBottom:Boolean = this.m_overlayHistory.scrollV >= this.m_overlayHistory.maxScrollV;
               this.m_overlayHistory.text = output == "" ? "Say hello. Your conversation is private." : output;
               if(atBottom || this.m_overlayHistoryKey == "") this.m_overlayHistory.scrollV = this.m_overlayHistory.maxScrollV;
               this.m_overlayHistoryKey = output;
            }
            if(OverlayActive() && incoming != "0" && Number(incoming) > Number(header[2])) this.overlayQueue("read|" + this.m_overlayFriend + "|" + incoming);
         }
         else if(header[0] == "A" && header.length == 4 && (header[3].length == 13824 || header[3] == ""))
         {
            this.m_overlayAvatars[header[1] + "/" + header[2]] = header[3];
            this.overlayFriends();
         }
         else if(header[0] == "D" && header.length == 3 && this.m_overlayPending != null && header[1] == this.m_overlayPending.id)
         {
            if(this.m_overlayFriend == this.m_overlayPending.target && this.m_overlayInput.text == this.m_overlayPending.text) this.m_overlayInput.text = "";
            this.m_overlayDrafts[this.m_overlayPending.target] = "";
            this.overlayQueue("history|" + this.m_overlayPending.target);
            this.m_overlayPending = null;
            if(this.m_overlayStatus != null) this.m_overlayStatus.text = "Message sent.";
         }
         else if(header[0] == "E" && header.length == 2 && this.m_overlayStatus != null) this.m_overlayStatus.text = decodeURIComponent(header[1]);
         return true;
      }
