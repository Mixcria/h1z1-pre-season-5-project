# ReferenceData handler trace (attach during Connecting, host started with a bootstrap delay so
# the packets arrive after the attach). Prints the packet bytes, the parsed type-name object
# (FUN_140a12af0 result at [rbp-0x49]: u32 id|flag, u32 index) and the ids the handler computes
# for its own literals, then detaches so the actor-creation integrity guard never sees a debugger.
set pagination off
set confirm off
set breakpoint pending on
set print thread-events off
handle SIGILL nostop noprint pass
handle SIGSEGV nostop noprint pass

# FUN_140b055c0(client, data, len): ReferenceData handler entry
break *0x140b055c0
commands
  silent
  printf "TRACE REFDATA_ENTER client=%p data=%p len=%d\n", $rcx, $rdx, $r8d
  x/40xb $rdx
  continue
end

# after FUN_140a12af0(&nameObject, &stream)
break *0x140b05659
commands
  silent
  printf "TRACE REFDATA_NAME id=0x%08x index=0x%08x cursor=%p\n", *(unsigned int *)($rbp-0x49), *(unsigned int *)($rbp-0x45), *(void **)($rbp+0xf)
  continue
end

# after FUN_140981390("ItemClasses", -1, 1, 0, 0, 0)
break *0x140b056f9
commands
  silent
  printf "TRACE REFDATA_LOOKUP ItemClasses -> 0x%08x (ours 0x%08x)\n", $eax, *(unsigned int *)($rbp-0x49)
  continue
end

# after FUN_140981390("ProfileDefinitions", -1, 1, 0, 0, 0)
break *0x140b058b2
commands
  silent
  printf "TRACE REFDATA_LOOKUP ProfileDefinitions -> 0x%08x (ours 0x%08x idx 0x%08x)\n", $eax, *(unsigned int *)($rbp-0x49), *(unsigned int *)($rbp-0x45)
  continue
end

# Inside FUN_140a12af0: the call FUN_140981390(namePtr, -1, flag, 0, &len, 0) that interns the
# packet's type name (only packet names come through here, unlike the per-frame lookups).
break *0x140a12b8d
commands
  silent
  printf "TRACE PKTNAME_INTERN str="
  x/s $rcx
  printf "TRACE PKTNAME_INTERN p2=0x%x flag=%d p4=%d len=%d\n", $edx, $r8d, $r9d, **(int **)($rsp+0x20)
  continue
end

break *0x140a12b92
commands
  silent
  printf "TRACE PKTNAME_INTERN -> id 0x%08x\n", $eax
  continue
end

# "Received ReferenceData type=%s, but no handler!"
break *0x140b05b94
commands
  silent
  printf "TRACE REFDATA_NO_HANDLER\n"
  detach
  quit
end

continue
printf "TRACE STOP pc=%p\n", $pc
bt 8
