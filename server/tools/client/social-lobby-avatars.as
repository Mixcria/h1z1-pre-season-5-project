      public static function LocalAvatars() : void
      {
         if(m_local == null) return;
         m_local.localAvatarList(m_local.m_groupList);
         m_local.localAvatarList(m_local.m_miniGroupList);
         m_local.localAvatarList(m_local.m_friendsList);
      }

      private function localAvatarList(list:DisplayObjectContainer) : void
      {
         if(list == null) return;
         for(var i:int = 0; i < list.numChildren; i++)
         {
            var child:DisplayObjectContainer = list.getChildAt(i) as DisplayObjectContainer;
            if(child == null) continue;
            if(child is ListItemRenderer)
            {
               var row:Object = Object(child).data;
               if(!("m_placeholderIcon" in child)) continue;
               var placeholder:DisplayObject = Object(child).m_placeholderIcon;
               if(placeholder == null) continue;
               var holder:Sprite = child.getChildByName("cranberryAvatarHolder") as Sprite;
               if(row == null || row.AccountId == null || row.AvatarVersion == null || row.AvatarVersion == "")
               { if(holder != null) child.removeChild(holder); continue; }
               if(holder == null)
               {
                  holder = new Sprite(); holder.name = "cranberryAvatarHolder"; holder.mouseEnabled = false; holder.mouseChildren = false;
                  child.addChild(holder);
               }
               var bounds:Rectangle = placeholder.getBounds(child);
               holder.x = bounds.x; holder.y = bounds.y;
               var painted:Boolean = UIConsoleManager.OverlayAvatar(holder,String(row.AccountId),String(row.AvatarVersion),bounds.width,bounds.height);
               holder.visible = painted;
               if(painted) placeholder.visible = false;
            }
            else this.localAvatarList(child);
         }
      }
