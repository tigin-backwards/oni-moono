# The Steam double's Workshop part (gen.py --ugc; U60 QA spec M1 and M2): one subscribed item,
# installed at a folder or a file, its details query answered, and a forced download of its
# content (UGCDownloadToLocation) served from a file. Configured by the environment:
#   ONI_STEAMSTUB_UGC_ID        the item's PublishedFileId (decimal); unset: no subscription
#   ONI_STEAMSTUB_UGC_PATH      what GetItemInstallInfo answers (a folder, or a legacy zip)
#   ONI_STEAMSTUB_UGC_TITLE     the details' title (default "e2e workshop item")
#   ONI_STEAMSTUB_UGC_DOWNLOAD  the file UGCDownloadToLocation copies to the location it is given
# Call results are delivered through ManualDispatch, as Steamworks.NET's CallbackDispatcher
# reads them: a SteamAPICallCompleted_t (703) naming the call, then GetAPICallResult fills the
# result (SteamUGCQueryCompleted_t 3401, RemoteStorageDownloadUGCResult_t 1317) at the offsets of
# the game's Linux Steamworks.NET (pack 4). x86-64 System V; links against libc.
.intel_syntax noprefix
.text

# rax = the configured id, or 0.
ugc_id:
  sub rsp, 8
  lea rdi, [rip + env_id]
  call getenv@PLT
  test rax, rax
  jz 1f
  mov rdi, rax
  xor esi, esi
  mov edx, 10
  call strtoull@PLT
1:
  add rsp, 8
  ret

.globl SteamAPI_ISteamUGC_GetNumSubscribedItems
.type SteamAPI_ISteamUGC_GetNumSubscribedItems, @function
SteamAPI_ISteamUGC_GetNumSubscribedItems:
  sub rsp, 8
  call ugc_id
  add rsp, 8
  test rax, rax
  setnz al
  movzx eax, al
  ret

# (self, PublishedFileId_t* items, uint max)
.globl SteamAPI_ISteamUGC_GetSubscribedItems
.type SteamAPI_ISteamUGC_GetSubscribedItems, @function
SteamAPI_ISteamUGC_GetSubscribedItems:
  push rbx
  push r12
  sub rsp, 8
  mov rbx, rsi
  mov r12d, edx
  call ugc_id
  test rax, rax
  jz 1f
  test r12d, r12d
  jz 2f
  mov [rbx], rax
  mov eax, 1
  jmp 3f
1:
2:
  xor eax, eax
3:
  add rsp, 8
  pop r12
  pop rbx
  ret

# (self, id): subscribed | installed for the item.
.globl SteamAPI_ISteamUGC_GetItemState
.type SteamAPI_ISteamUGC_GetItemState, @function
SteamAPI_ISteamUGC_GetItemState:
  push rbx
  mov rbx, rsi
  call ugc_id
  test rax, rax
  jz 1f
  cmp rax, rbx
  jne 1f
  mov eax, 5
  pop rbx
  ret
1:
  xor eax, eax
  pop rbx
  ret

# (self, id, ulong* size, char* folder, uint cch, uint* timestamp)
.globl SteamAPI_ISteamUGC_GetItemInstallInfo
.type SteamAPI_ISteamUGC_GetItemInstallInfo, @function
SteamAPI_ISteamUGC_GetItemInstallInfo:
  push rbx
  push r12
  push r13
  push r14
  push r15
  mov rbx, rsi
  mov r12, rdx
  mov r13, rcx
  mov r14d, r8d
  mov r15, r9
  call ugc_id
  test rax, rax
  jz 9f
  cmp rax, rbx
  jne 9f
  test r14d, r14d
  jz 9f
  lea rdi, [rip + env_path]
  call getenv@PLT
  test rax, rax
  jz 9f
  test r12, r12
  jz 1f
  mov qword ptr [r12], 0
1:
  test r15, r15
  jz 2f
  mov dword ptr [r15], 0
2:
  mov rdi, r13
  mov rsi, rax
  mov edx, r14d
  call strncpy@PLT
  mov eax, r14d
  mov byte ptr [r13 + rax - 1], 0
  mov eax, 1
  jmp 8f
9:
  xor eax, eax
8:
  pop r15
  pop r14
  pop r13
  pop r12
  pop rbx
  ret

.globl SteamAPI_ISteamUGC_CreateQueryUGCDetailsRequest
.type SteamAPI_ISteamUGC_CreateQueryUGCDetailsRequest, @function
SteamAPI_ISteamUGC_CreateQueryUGCDetailsRequest:
  mov eax, 1
  ret

.globl SteamAPI_ISteamUGC_ReleaseQueryUGCRequest
.type SteamAPI_ISteamUGC_ReleaseQueryUGCRequest, @function
SteamAPI_ISteamUGC_ReleaseQueryUGCRequest:
  mov eax, 1
  ret

# (self, handle): call 0x100, its result pending.
.globl SteamAPI_ISteamUGC_SendQueryUGCRequest
.type SteamAPI_ISteamUGC_SendQueryUGCRequest, @function
SteamAPI_ISteamUGC_SendQueryUGCRequest:
  mov dword ptr [rip + pending], 1
  mov eax, 0x100
  ret

# (self, handle, index, SteamUGCDetails_t* details)
.globl SteamAPI_ISteamUGC_GetQueryUGCResult
.type SteamAPI_ISteamUGC_GetQueryUGCResult, @function
SteamAPI_ISteamUGC_GetQueryUGCResult:
  push rbx
  mov rbx, rcx
  mov rdi, rbx
  xor esi, esi
  mov edx, 9764
  call memset@PLT
  call ugc_id
  mov [rbx], rax
  mov dword ptr [rbx + 8], 1
  mov dword ptr [rbx + 16], 457140
  mov dword ptr [rbx + 20], 457140
  mov qword ptr [rbx + 9208], 7
  mov qword ptr [rbx + 9216], -1
  lea rdi, [rip + env_title]
  call getenv@PLT
  test rax, rax
  jnz 1f
  lea rax, [rip + default_title]
1:
  lea rdi, [rbx + 24]
  mov rsi, rax
  mov edx, 128
  call strncpy@PLT
  mov byte ptr [rbx + 24 + 128], 0
  mov eax, 1
  pop rbx
  ret

# (self, UGCHandle_t content, char* location, uint priority): copies ONI_STEAMSTUB_UGC_DOWNLOAD to
# the location; call 0x200, its result pending. Answers k_uAPICallInvalid (0) without a source.
.globl SteamAPI_ISteamRemoteStorage_UGCDownloadToLocation
.type SteamAPI_ISteamRemoteStorage_UGCDownloadToLocation, @function
SteamAPI_ISteamRemoteStorage_UGCDownloadToLocation:
  push rbx
  push r12
  push r13
  push r14
  sub rsp, 8
  mov r12, rdx
  lea rdi, [rip + env_download]
  call getenv@PLT
  test rax, rax
  jz 9f
  mov rdi, rax
  xor esi, esi                      # O_RDONLY
  xor eax, eax
  call open@PLT
  test eax, eax
  js 9f
  mov ebx, eax
  mov rdi, r12
  mov esi, 0x241                    # O_WRONLY | O_CREAT | O_TRUNC
  mov edx, 0644
  xor eax, eax
  call open@PLT
  test eax, eax
  js 7f
  mov r13d, eax
  xor r14d, r14d
1:
  mov edi, ebx
  lea rsi, [rip + copy_buffer]
  mov edx, 65536
  call read@PLT
  test rax, rax
  jle 2f
  add r14d, eax
  mov edi, r13d
  lea rsi, [rip + copy_buffer]
  mov rdx, rax
  call write@PLT
  jmp 1b
2:
  mov edi, r13d
  call close@PLT
  mov edi, ebx
  call close@PLT
  mov [rip + downloaded], r14d
  mov dword ptr [rip + pending], 2
  mov eax, 0x200
  jmp 8f
7:
  mov edi, ebx
  call close@PLT
9:
  xor eax, eax
8:
  add rsp, 8
  pop r14
  pop r13
  pop r12
  pop rbx
  ret

# (pipe, CallbackMsg_t* msg): the pending call's SteamAPICallCompleted_t, once.
.globl SteamAPI_ManualDispatch_GetNextCallback
.type SteamAPI_ManualDispatch_GetNextCallback, @function
SteamAPI_ManualDispatch_GetNextCallback:
  mov eax, [rip + pending]
  test eax, eax
  jz 9f
  lea rdx, [rip + completed]
  cmp eax, 1
  jne 1f
  mov qword ptr [rdx], 0x100
  mov dword ptr [rdx + 8], 3401
  mov dword ptr [rdx + 12], 280
  jmp 2f
1:
  mov qword ptr [rdx], 0x200
  mov dword ptr [rdx + 8], 1317
  mov dword ptr [rdx + 12], 288
2:
  mov dword ptr [rsi], 1
  mov dword ptr [rsi + 4], 703
  mov [rsi + 8], rdx
  mov dword ptr [rsi + 16], 16
  mov dword ptr [rip + pending], 0
  mov eax, 1
  ret
9:
  xor eax, eax
  ret

# (pipe, SteamAPICall_t call, void* result, int size, int expected, bool* failed)
.globl SteamAPI_ManualDispatch_GetAPICallResult
.type SteamAPI_ManualDispatch_GetAPICallResult, @function
SteamAPI_ManualDispatch_GetAPICallResult:
  push rbx
  push r12
  push r13
  mov rbx, rdx
  mov r12d, r8d
  mov r13, r9
  mov rdi, rbx
  xor esi, esi
  movsxd rdx, ecx
  call memset@PLT
  test r13, r13
  jz 1f
  mov byte ptr [r13], 0
1:
  cmp r12d, 3401
  jne 2f
  mov qword ptr [rbx], 1            # m_handle
  mov dword ptr [rbx + 8], 1        # m_eResult: k_EResultOK
  mov dword ptr [rbx + 12], 1       # m_unNumResultsReturned
  mov dword ptr [rbx + 16], 1       # m_unTotalMatchingResults
  mov eax, 1
  jmp 8f
2:
  cmp r12d, 1317
  jne 9f
  mov dword ptr [rbx], 1            # m_eResult: k_EResultOK
  mov qword ptr [rbx + 4], 7        # m_hFile
  mov dword ptr [rbx + 12], 457140  # m_nAppID
  mov eax, [rip + downloaded]
  mov [rbx + 16], eax               # m_nSizeInBytes
  mov eax, 1
  jmp 8f
9:
  xor eax, eax
8:
  pop r13
  pop r12
  pop rbx
  ret

.section .rodata
env_id: .asciz "ONI_STEAMSTUB_UGC_ID"
env_path: .asciz "ONI_STEAMSTUB_UGC_PATH"
env_title: .asciz "ONI_STEAMSTUB_UGC_TITLE"
env_download: .asciz "ONI_STEAMSTUB_UGC_DOWNLOAD"
default_title: .asciz "e2e workshop item"
.bss
.balign 16
pending: .zero 4
downloaded: .zero 4
.balign 16
completed: .zero 16
copy_buffer: .zero 65536
.section .note.GNU-stack,"",@progbits
