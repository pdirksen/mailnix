import { i18n } from '@/i18n'
import { provideRuleEditor } from '@/rules/editorContext'
import { newAction, type RuleAction } from '@/rules/model'
import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import { defineComponent, h, ref } from 'vue'
import ActionList from './ActionList.vue'

function mountActions(actions: RuleAction[], gmail: boolean) {
  const Harness = defineComponent({
    setup() {
      provideRuleEditor({
        catalog: ref(new Map()),
        lists: ref([]),
        rules: ref([]),
        errors: ref({}),
        folders: ref([]),
        delimiter: ref('/'),
        labels: ref(gmail),
      })
      return () => h(ActionList, { modelValue: actions })
    },
  })
  return mount(Harness, { global: { plugins: [i18n] } })
}

describe('ActionList', () => {
  it('speaks of labels for Gmail accounts', () => {
    i18n.global.locale.value = 'en'
    const actions = [newAction('CopyTo'), newAction('MoveTo')]

    expect(mountActions(actions, true).text()).toContain('Add label')
    expect(mountActions(actions, true).text()).toContain('Move to label')
    expect(mountActions(actions, false).text()).toContain('Move to folder')
    expect(mountActions(actions, false).text()).not.toContain('label')
  })
})
