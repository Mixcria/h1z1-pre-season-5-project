set pagination off
set confirm off
set breakpoint pending on
handle SIGILL nostop noprint pass
handle SIGSEGV nostop noprint pass

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
