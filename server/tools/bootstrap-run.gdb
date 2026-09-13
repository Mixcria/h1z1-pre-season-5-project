# Runs the August client under gdb and traces the zone bootstrap: the SendSelfToClient parse
# and player creation (FUN_140af3950 case 3), PostInitialize's world init (FUN_140b36780 ->
# FUN_1423c7610), the heightfield world's Init, zone asset opens and the local-actor creator
# (FUN_140acccd0). Unlike bootstrap-trace.gdb this variant stops on an access violation and
# prints the registers and a backtrace, so a crash site is captured in one run.
#
#   cd C:\Aug2017\Client
#   gdb -batch -x C:\Aug2017\Server\tools\bootstrap-run.gdb H1Z1.exe > C:\Aug2017\logs\gdb-bootstrap-<stamp>.out.log 2> ...err.log
#
# The client still needs its splash / title-screen key presses (run-client.ps1 posts them; use
# press-client-key.ps1 for a gdb-launched client).
set pagination off
set confirm off
set breakpoint pending on
set print thread-events off
handle SIGILL nostop noprint pass

break *0x140af4223
commands
  silent
  printf "TRACE SELF_PARSE_RETURN al=%u\n", $eax & 0xff
  continue
end

break *0x140af4286
commands
  silent
  printf "TRACE SELF_CREATED player=%p\n", $r15
  continue
end

# FUN_140a31140 returns here (call at 0x140af42ee in case 3): the loader did not abort
break *0x140af42f3
commands
  silent
  printf "TRACE SELF_LOADER_RETURNED\n"
  continue
end

break *0x140b3684d
commands
  silent
  printf "TRACE WORLD_CALL type=%d name=", $r8d
  x/s $rdx
  continue
end

break *0x140b36852
commands
  silent
  printf "TRACE WORLD_RETURN al=%u storedType=%d name=", $eax & 0xff, *(int *)($rdi+0x32298)
  x/s *(void **)($rdi+0x32288)
  continue
end

break *0x142220f80
commands
  silent
  printf "TRACE HEIGHTFIELD_INIT name="
  x/s $rdx
  continue
end

break *0x142221020
commands
  silent
  printf "TRACE HEIGHTFIELD_BASE_RETURN al=%u\n", $eax & 0xff
  continue
end

break *0x142d4f240
commands
  silent
  printf "TRACE ZONE_ASSET_OPEN name="
  x/s *(void **)($rcx+0x28)
  continue
end

break *0x142d4f2b5
commands
  silent
  printf "TRACE ZONE_ASSET_RESULT raw=%d\n", $eax
  continue
end

break *0x140acccd0
commands
  silent
  printf "TRACE ACTOR_CREATE rcx=%p rdx=%p\n", $rcx, $rdx
  continue
end

set args inifile=ClientConfig.ini sessionid=lp2.11112222333344445555666677778888.1787936413.9999AAAABBBBCCCC Internationalization:Locale=en_US LaunchPad:Ufp=0 LaunchPad:SessionId=0 LaunchPad:Locale=en_US
run
printf "TRACE STOP pc=%p\n", $pc
info registers rip rsp rax rbx rcx rdx rsi rdi r8 r9 r14 r15
bt 25
