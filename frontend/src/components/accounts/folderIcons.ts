import type { FolderInfo } from '@/stores/accounts'
import {
  ArchiveIcon,
  FlagIcon,
  FolderIcon,
  InboxIcon,
  MailsIcon,
  PencilLineIcon,
  SendIcon,
  ShieldAlertIcon,
  Trash2Icon,
} from '@lucide/vue'
import type { Component } from 'vue'

type SpecialUse = NonNullable<FolderInfo['specialUse']>

export const SPECIAL_FOLDER_ICONS: Record<SpecialUse, Component> = {
  Inbox: InboxIcon,
  Sent: SendIcon,
  Drafts: PencilLineIcon,
  Trash: Trash2Icon,
  Junk: ShieldAlertIcon,
  Archive: ArchiveIcon,
  All: MailsIcon,
  Flagged: FlagIcon,
}

export function folderIcon(specialUse: FolderInfo['specialUse']): Component {
  return specialUse ? SPECIAL_FOLDER_ICONS[specialUse] : FolderIcon
}
