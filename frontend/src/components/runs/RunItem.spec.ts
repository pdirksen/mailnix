import { i18n } from '@/i18n'
import { apiMock, apiResult } from '@/test/apiMock'
import { testRun } from '@/test/runFixtures'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import RunItem from './RunItem.vue'

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  api: (await import('@/test/apiMock')).apiMock,
}))

const router = createRouter({
  history: createMemoryHistory(),
  routes: [
    { path: '/', component: { template: '<div />' } },
    { path: '/activity/pending/:id', name: 'run-review', component: { template: '<div />' } },
  ],
})

function mountItem(run = testRun(), initiallyOpen = false) {
  return mount(RunItem, {
    props: { run, initiallyOpen },
    global: { plugins: [i18n, router] },
    attachTo: document.body,
  })
}

describe('RunItem', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.resetAllMocks()
    i18n.global.locale.value = 'en'
  })

  it('summarizes the run and offers undo', async () => {
    const wrapper = mountItem()

    expect(wrapper.text()).toContain('Run manually')
    expect(wrapper.text()).toContain('4 actions on 3 of 10 messages')
    await wrapper.get('[data-testid="undo-run"]').trigger('click')
    expect(wrapper.emitted('undo')?.[0]?.[0]).toMatchObject({ id: 'run-1' })
    expect(wrapper.find('[data-testid="review-run"]').exists()).toBe(false)
  })

  it('links runs waiting for confirmation to the review page', () => {
    const wrapper = mountItem(testRun({ state: 'AwaitingConfirmation', canUndo: false }))

    expect(wrapper.text()).toContain('Waiting for confirmation')
    expect(wrapper.get('[data-testid="review-run"]').attributes('href')).toBe('/activity/pending/run-1')
    expect(wrapper.find('[data-testid="undo-run"]').exists()).toBe(false)
  })

  it('shows undone runs as undone', () => {
    const wrapper = mountItem(testRun({ undoneByRunId: 'undo-1', canUndo: false }))

    expect(wrapper.text()).toContain('Undone')
  })

  it('shows the server-side preselection of a run', async () => {
    apiMock.GET.mockResolvedValue(apiResult(200, { run: testRun(), pending: [], actions: [] }))
    const wrapper = mountItem(testRun({ trigger: 'Scheduled', searchFilter: 'SUBJECT "invoice"' }), true)
    await flushPromises()

    expect(wrapper.text()).toContain('Scheduled')
    expect(wrapper.get('[data-testid="search-filter"]').text()).toContain('SUBJECT "invoice"')
  })

  it('summarizes Trash purges, which cannot be undone', () => {
    const wrapper = mountItem(testRun({ trigger: 'Purge', folder: 'Trash', executedActions: 3, messagesExamined: 40, canUndo: false }))

    expect(wrapper.text()).toContain('Trash purge')
    expect(wrapper.text()).toContain('3 of 40 messages in Trash deleted permanently')
    expect(wrapper.find('[data-testid="undo-run"]').exists()).toBe(false)
  })

  it('loads the actions when opened', async () => {
    apiMock.GET.mockResolvedValue(
      apiResult(200, {
        run: testRun(),
        pending: [],
        actions: [
          {
            id: 'a1',
            ruleId: 'rule-1',
            ruleName: 'Newsletters',
            action: 'MoveTo',
            keyword: null,
            sourceFolder: 'INBOX',
            targetFolder: 'Newsletters',
            from: 'news@shop.example',
            subject: 'Weekly deals',
            dryRun: false,
            success: false,
            error: 'Folder is read-only',
            createdAt: '2026-10-24T10:00:02Z',
            undoneAt: null,
          },
        ],
      }),
    )
    const wrapper = mountItem(testRun(), true)
    await flushPromises()

    expect(apiMock.GET).toHaveBeenCalledWith('/api/runs/{id}', { params: { path: { id: 'run-1' } } })
    const row = wrapper.get('[data-testid="action-MoveTo"]')
    expect(row.text()).toContain('Weekly deals')
    expect(row.text()).toContain('Newsletters')
    expect(row.text()).toContain('Failed')
    expect(row.text()).toContain('Folder is read-only')
  })
})
