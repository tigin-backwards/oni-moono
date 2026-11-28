/* The crash window's fonts (SIL OFL 1.1, NOTICE.txt), the icon and the picture, in the Linux executable
 * (crash_window_linux.c); build-linux.sh assembles this beside the files it names. */
    .section .rodata
    .balign 16
#define BLOB(name, file) \
    .global name; .hidden name; .type name, @object; name: .incbin file; name##_end: \
    .balign 8; .global name##_size; .hidden name##_size; name##_size: .quad name##_end - name

BLOB(moono_font_regular, "Gabarito-Regular.ttf")
BLOB(moono_font_medium, "Gabarito-Medium.ttf")
BLOB(moono_font_semibold, "Gabarito-SemiBold.ttf")
BLOB(moono_font_mono, "RedHatMono-Regular.ttf")
BLOB(moono_icon, "Moo.ico")
BLOB(moono_picture, "crash-moo.png")

    .section .note.GNU-stack, "", @progbits
