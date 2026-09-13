      public static function LocalActive() : Boolean
      {
         return m_local != null;
      }

      public static function LocalLeader() : Boolean
      {
         return m_localSelf != "0" && m_localSelf == m_localLeader;
      }

      public static function LocalCanQueue(size:int) : Boolean
      {
         return LocalLeader() && m_localMembers.length <= (size > 0 ? size : 1);
      }

      public static function LocalSend(action:String) : void
      {
         UIBindingSystem.DispatchWallOfData("CRANBERRY_SOCIAL_V1",action);
      }

      public static function LocalNotice(text:String) : void
      {
         if(m_local == null) return;
         var event:GameEvent = new GameEvent(GameEvent.EVENT_PLAY_NOTIFICATION2);
         event.data = ["SystemMessage",UIBindingChat.MakeHtmlSafe(text)];
         m_local.m_stage.dispatchEvent(event);
      }

      public static function LocalShowInvite(invite:String, inviter:String) : void
      {
         if(invite == "")
         {
            m_localInvite = "";
            return;
         }
         if(invite == m_localInvite)
         {
            LocalSend("seen " + invite);
            return;
         }
         // Wait for an existing modal to close. The server retries unacknowledged
         // invitations; placing one behind an unrelated modal can outlive its token.
         if(MainController.getWidgetManager().isWidgetLoaded(WidgetNames.CONFIRMATION_DIALOG_WINDOW)
            || MainController.getWidgetManager().isWidgetLoading(WidgetNames.CONFIRMATION_DIALOG_WINDOW)) return;
         MainController.showSystemMessage({
            "message":UIBindingChat.MakeHtmlSafe(inviter) + " invited you to their group.",
            "accept":UIBindingLocale.translateCodeString("UI.Accept"),
            "decline":UIBindingLocale.translateCodeString("UI.Decline"),
            "timeout":60,
            "callback":function(accepted:Boolean):void
            {
               if(invite == m_localInvite)
                  LocalSend((accepted ? "accept " : "decline ") + invite);
            }
         });
         m_localInvite = invite;
         LocalSend("seen " + invite);
      }

      public static function LocalReceive(text:String) : Boolean
      {
         if(text.indexOf("@cranberry/social/1;") != 0) return false;
         if(m_local == null || text.length > 262144) return true;
         var rows:Array = text.substr("@cranberry/social/1;".length).split(";");
         var header:Array = String(rows[0]).split("|");
         if(header[0] == "N" && header.length == 2)
         {
            LocalNotice(decodeURIComponent(header[1]));
            return true;
         }
         if(header[0] != "S" || header.length != 7) return true;
         var members:Array = [];
         var friends:Array = [];
         var invite:String = "";
         var inviter:String = "";
         var fields:Array;
         var row:String;
         for each(row in rows.slice(1))
         {
            fields = row.split("|");
            if(fields[0] == "M" && fields.length == 5)
            {
               members.push({"UserId":fields[1],"Name":decodeURIComponent(fields[2]),"NickName":"",
                  "CharacterName":decodeURIComponent(fields[3]),"AvatarId":0,"IsOwner":fields[1] == header[2],
                  "IsReady":fields[4] == "Menu","IsInGame":fields[4] != "Menu","IsMutedForMe":false});
            }
            else if(fields[0] == "F" && fields.length == 6)
            {
               friends.push({"UserId":fields[1],"Name":decodeURIComponent(fields[2]),"NickName":"",
                  "CharacterName":decodeURIComponent(fields[3]),"AvatarId":0,"InGame":fields[4] != "Offline",
                  "State":fields[4] == "Offline" ? 0 : 1,"Status":fields[4],"Available":fields[5] == "1"});
            }
            else if(fields[0] == "I" && fields.length == 3)
            {
               invite = fields[1];
               inviter = decodeURIComponent(fields[2]);
            }
         }
         m_localSelf = header[1];
         m_localLeader = header[2];
         if(header[3] != "Menu" && int(header[4]) > 0)
         {
            SharedGlobalData.GetInstance().isTeam2 = header[4] == "2";
            SharedGlobalData.GetInstance().isInGroupGame = true;
         }
         m_localMembers = members;
         m_localFriends = friends;
         // Deliver the action before refreshing list bindings. A renderer failure
         // must not consume a one-shot invitation snapshot without opening it.
         LocalShowInvite(invite,inviter);
         m_localFriends.sortOn(["InGame","Name"],[Array.DESCENDING | Array.NUMERIC,Array.CASEINSENSITIVE]);
         m_local.handleLobbyDataChange(null);
         m_local.handleFriendsDataChange(null);
         var queueKey:String = header[5] + "/" + header[6];
         if(header[3] == "Queued" && !LocalLeader() && int(header[5]) > 0 && queueKey != m_localQueue)
         {
            m_localQueue = queueKey;
            var queueEvent:GameEvent = new GameEvent(GameEvent.EVENT_ON_STEAM_LOBBY_START_GAME);
            queueEvent.data = [int(header[5]),"cranberry",int(header[4])];
            m_local.m_stage.dispatchEvent(queueEvent);
         }
         if(header[3] == "Menu") m_localQueue = "";
         return true;
      }

      protected function processRightClickMenu(param1:IUIDataBinding, param2:CoreList) : void
      {
         this.m_rightClickList = param2;
         if(param2 == null) return;
         param2.focusable = false;
         // Both mouse and controller activation have item data. A selection-change
         // event can be deferred, suppressed for the same index, or caused by binding.
         param2.removeEventListener(ListEvent.INDEX_CHANGE,this.handleRightClickMenuIndexChange,false);
         param2.removeEventListener(ListEvent.ITEM_CLICK,this.handleRightClickMenuIndexChange,false);
         if(param1 != null)
            param2.addEventListener(ListEvent.ITEM_CLICK,this.handleRightClickMenuIndexChange,false,0,true);
      }

      private function pollLocalSocial(event:TimerEvent = null) : void
      {
         LocalSend(m_localSelf == "0" ? "hello" : "state");
      }

      protected function createLobby() : void
      {
         this.pollLocalSocial();
      }

      protected function handleLobbyDataChange(param1:uiDBEvent) : void
      {
         if(!this.m_isInitalized || this.m_bindings.length == 0) return;
         var rows:Array = m_localMembers.concat();
         var self:Array = [];
         var others:Array = [];
         var row:Object;
         for each(row in rows)
         {
            row.NumLobbyMembers = rows.length;
            if(row.UserId == m_localSelf) self.push(row); else others.push(row);
         }
         rows = self.concat(others);
         this.m_numGroupMembers = rows.length;
         this.m_bindings[BINDING_LOBBY_MEMBER_NAME].setValue(rows.length > 0 ? rows[0].Name : "");
         while(rows.length < LOBBY_MAX)
         {
            rows.push({"UserId":"0","Name":UIBindingLocale.translateCodeString("UI.Grouping.Empty"),
               "NickName":"","AvatarId":0,"IsOwner":false,"IsReady":true});
         }
         this.m_bindings[BINDING_LOBBY_DATA].setValue(new DataProvider(rows));
         this.m_bindings[BINDING_LOBBY_MINI_DATA].setValue(new DataProvider(rows));
      }

      protected function handleFriendsDataChange(param1:uiDBEvent) : void
      {
         if(!this.m_isInitalized || this.m_bindings.length == 0) return;
         this.m_bindings[BINDING_FRIENDS_DATA].setValue(new DataProvider(m_localFriends));
         if(this.m_friendsButton)
         {
            this.m_friendsButton.enabled = true;
            this.m_friendsButton.count.text = "(" + m_localFriends.length + ")";
         }
      }

      protected function handleRecentDataChange(param1:uiDBEvent) : void
      {
         if(this.m_recentButton) this.updateListHeaderButton(this.m_recentButton,0);
      }

      protected function handleSuggestedDataChange(param1:uiDBEvent) : void
      {
         if(this.m_suggestedButton) this.updateListHeaderButton(this.m_suggestedButton,0);
      }

      protected function handleLobbyMenuIndexChange(param1:ListEvent) : void
      {
         if(param1.index < 0 || this.m_isConsole && !this.m_groupList.focusable) return;
         var member:LobbyData = new LobbyData(param1.itemData);
         this.m_bindings[BINDING_LOBBY_MEMBER_NAME].setValue(member.name);
         this.m_bindings[BINDING_RIGHTCLICK_VISIBLE].setValue(false);
         if(member.userId == "0")
         {
            this.focusFriends();
            return;
         }
         if(member.userId != m_localSelf || m_localMembers.length < 2) return;
         var renderer:ListItemRenderer = param1.itemRenderer as ListItemRenderer;
         var list:CoreList = param1.target as CoreList;
         this.m_returnToFriends = false;
         this.m_bindings[BINDING_RIGHTCLICK_DATA].setValue(new DataProvider([this.getRightClickData(LEAVE_LOBBY,member)]));
         this.m_bindings[BINDING_RIGHTCLICK_POSITION].setValue({"y":list.y + renderer.y - 20});
         this.m_bindings[BINDING_RIGHTCLICK_VISIBLE].setValue(true);
         this.focusRightClick();
      }

      protected function handleFriendsMenuItemPress(param1:ListEvent) : void
      {
         if(param1.index < 0 || this.m_isConsole && !this.m_friendsList.focusable) return;
         var friend:FriendData = new FriendData(param1.itemData);
         var renderer:ListItemRenderer = param1.itemRenderer as ListItemRenderer;
         var list:CoreList = param1.target as CoreList;
         var disabled:Boolean = !LocalLeader() || m_localMembers.length >= LOBBY_MAX || !param1.itemData.Available;
         this.m_returnToFriends = true;
         this.m_bindings[BINDING_RIGHTCLICK_DATA].setValue(new DataProvider([this.getRightClickData(INVITE_TO_LOBBY,friend,disabled)]));
         this.m_bindings[BINDING_RIGHTCLICK_POSITION].setValue({"y":list.y + renderer.y + 110});
         this.m_bindings[BINDING_RIGHTCLICK_VISIBLE].setValue(true);
         this.focusRightClick();
      }

      protected function handleRightClickMenuIndexChange(param1:ListEvent) : void
      {
         if(param1.index < 0 || this.m_isConsole && !this.m_rightClickList.focusable) return;
         var row:Object = param1.itemData;
         if(row == null || !row.enabled) return;
         if(row.type == INVITE_TO_LOBBY) LocalSend("invite " + row.data.userId);
         else if(row.type == LEAVE_LOBBY) this.showLeaveLobbyConfirmation();
         this.m_bindings[BINDING_RIGHTCLICK_VISIBLE].setValue(false);
         if(this.m_returnToFriends) this.focusFriends();
         else this.m_stage.dispatchEvent(new MainMenuEvent(MainMenuEvent.SHOW_GROUP_ELEMENTS));
      }

      private function handleButtonEvent(param1:ButtonEvent) : void
      {
         var button:Button = param1.target as Button;
         if(!button || button != this.m_friendsButton) return;
         this.m_groupList.selectedIndex = -1;
         this.focusFriends();
         this.killTweens();
         this.animateButtons(button);
         this.handleFriendsDataChange(null);
      }

      protected function leaveLobby() : void
      {
         this.m_stage.dispatchEvent(new MainMenuEvent(MainMenuEvent.CANCEL_QUEUE));
         LocalSend("leave");
      }
