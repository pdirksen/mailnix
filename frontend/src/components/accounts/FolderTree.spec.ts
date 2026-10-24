import { i18n } from '@/i18n'
import type { FolderInfo } from '@/stores/accounts'
import { mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it } from 'vitest'
import FolderTree from './FolderTree.vue'

const folders: FolderInfo[] = [
  { path: 'INBOX', name: 'INBOX', parentPath: null, canSelect: true, specialUse: 'Inbox', messages: 20245, unread: 3 },
  { path: 'INBOX.Archive', name: 'Archive', parentPath: 'INBOX', canSelect: true, specialUse: 'Archive', messages: 1, unread: 0 },
  { path: 'INBOX.Papierkorb', name: 'Papierkorb', parentPath: 'INBOX', canSelect: true, specialUse: 'Trash', messages: 0, unread: 0 },
  { path: 'Shared', name: 'Shared', parentPath: null, canSelect: false, specialUse: null, messages: null, unread: null },
]

describe('FolderTree', () => {
  beforeEach(() => {
    i18n.global.locale.value = 'en'
  })

  function rowText(wrapper: ReturnType<typeof mount>, path: string) {
    const row = wrapper.findAll('li').find((li) => li.find(`[title="${path}"]`).exists())!
    return row.text().replace(/\s+/g, ' ')
  }

  it('shows counts with plural forms and number formatting', () => {
    const wrapper = mount(FolderTree, { props: { folders }, global: { plugins: [i18n] } })

    expect(rowText(wrapper, 'INBOX')).toContain('20,245 messages · 3 unread')
    expect(rowText(wrapper, 'INBOX.Archive')).toContain('1 message')
    expect(rowText(wrapper, 'INBOX.Archive')).not.toContain('1 messages')
  })

  it('only shows special-use badges that add information', () => {
    const wrapper = mount(FolderTree, { props: { folders }, global: { plugins: [i18n] } })

    expect(rowText(wrapper, 'INBOX.Archive')).toBe('Archive1 message')
    expect(rowText(wrapper, 'INBOX.Papierkorb')).toContain('Trash')
  })

  it('selects folders but not containers', async () => {
    const wrapper = mount(FolderTree, {
      props: { folders, selectable: true, modelValue: ['INBOX'] },
      global: { plugins: [i18n] },
      attachTo: document.body,
    })

    await wrapper.find('[aria-label="INBOX.Archive"]').trigger('click')

    expect(wrapper.emitted('update:modelValue')!.at(-1)).toEqual([['INBOX', 'INBOX.Archive']])
    expect(wrapper.find('[aria-label="Shared"]').attributes('disabled')).toBeDefined()
  })
})
