
      public static function OverlayCloseForInvite() : void
      {
         if(m_overlay != null) m_overlay.overlayClose();
      }

      private function overlayInviteFriend() : void
      {
         if(this.m_overlayFriend == "")
         {
            this.m_overlayStatus.text = "Select a friend to invite to your lobby.";
            return;
         }
         this.overlayQueue("invite|" + this.m_overlayFriend);
         this.m_overlayStatus.text = "Sending lobby invitation... Both players must be in the main menu.";
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
         this.overlayButton(this.m_overlayPanel,"INVITE TO LOBBY",792,106,220,this.overlayInviteFriend);
         this.m_overlayList = new Sprite(); this.m_overlayList.x = 28; this.m_overlayList.y = 108;
         this.m_overlayPanel.addChild(this.m_overlayList);
         this.m_overlayList.addEventListener(MouseEvent.MOUSE_WHEEL,this.overlayWheel,false,0,true);
         this.m_overlayPeer = this.overlayLabel(this.m_overlayPanel,"Select a friend",336,110,448,40,25);
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
         else if((header[0] == "E" || header[0] == "P") && header.length == 2 && this.m_overlayStatus != null) this.m_overlayStatus.text = decodeURIComponent(header[1]);
         return true;
      }
