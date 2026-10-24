<script setup lang="ts">
import FormField from '@/components/common/FormField.vue'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { toDraft } from '@/rules/model'
import { useListsStore } from '@/stores/lists'
import { type RuleTemplateInfo, useRulesStore } from '@/stores/rules'
import { ArrowLeftIcon, Loader2Icon, SparklesIcon } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { ApiError } from '@/api/client'

const props = defineProps<{ accountId: string }>()
const open = defineModel<boolean>('open', { required: true })

const { t } = useI18n()
const rules = useRulesStore()
const lists = useListsStore()
const router = useRouter()
const { describeRequestError } = useConnectionErrors()

const selected = ref<RuleTemplateInfo | null>(null)
const values = ref<Record<string, string>>({})
const errors = ref<Record<string, string>>({})
const busy = ref(false)

watch(open, async (isOpen) => {
  if (!isOpen) return
  selected.value = null
  await Promise.all([rules.loadTemplates(), lists.fetchAll()])
})

const domainLists = computed(() => lists.lists.filter((l) => l.kind !== 'Address'))

function choose(template: RuleTemplateInfo) {
  selected.value = template
  errors.value = {}
  values.value = Object.fromEntries(template.parameters.map((p) => [p.name, p.default ?? '']))
}

async function use() {
  if (!selected.value) return
  busy.value = true
  errors.value = {}
  try {
    const draft = await rules.draftFromTemplate(selected.value.id, t(`rules.templates.items.${selected.value.id}.name`), values.value)
    rules.pendingDraft = toDraft({ ...draft, enabled: true, stopProcessing: false, dryRun: false, sourceFolders: [] })
    open.value = false
    await router.push({ name: 'rule-new', query: { account: props.accountId } })
  } catch (error) {
    if (error instanceof ApiError && error.problem.errors) {
      errors.value = Object.fromEntries(Object.entries(error.problem.errors).map(([key, messages]) => [key.replace('parameters.', ''), messages[0] ?? '']))
    } else {
      errors.value = { _: describeRequestError(error).message }
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <Dialog v-model:open="open">
    <DialogContent class="sm:max-w-2xl">
      <DialogHeader>
        <DialogTitle>{{ selected ? $t(`rules.templates.items.${selected.id}.name`) : $t('rules.templates.title') }}</DialogTitle>
        <DialogDescription>{{ selected ? $t(`rules.templates.items.${selected.id}.description`) : $t('rules.templates.hint') }}</DialogDescription>
      </DialogHeader>

      <div v-if="!selected" class="grid max-h-[60vh] gap-3 overflow-y-auto sm:grid-cols-2">
        <button
          v-for="template in rules.templates"
          :key="template.id"
          type="button"
          class="hover:bg-accent/50 focus-visible:ring-ring/50 grid gap-1 rounded-lg border p-3 text-left outline-none focus-visible:ring-3"
          :data-testid="`template-${template.id}`"
          @click="choose(template)"
        >
          <span class="flex items-center gap-2 font-medium">
            <SparklesIcon class="text-muted-foreground size-4" />
            {{ $t(`rules.templates.items.${template.id}.name`) }}
          </span>
          <span class="text-muted-foreground text-sm">{{ $t(`rules.templates.items.${template.id}.description`) }}</span>
        </button>
      </div>

      <form v-else class="grid gap-4" novalidate @submit.prevent="use">
        <FormField
          v-for="parameter in selected.parameters"
          :id="`template-${parameter.name}`"
          :key="parameter.name"
          v-slot="aria"
          :label="$t(`rules.templates.parameters.${parameter.name}`)"
          :error="errors[parameter.name]"
        >
          <Select v-if="parameter.type === 'DomainList'" v-model="values[parameter.name]">
            <SelectTrigger :id="`template-${parameter.name}`" class="w-full" v-bind="aria">
              <SelectValue :placeholder="domainLists.length ? $t('rules.value.chooseList') : $t('rules.value.noLists')" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem v-for="list in domainLists" :key="list.id" :value="list.id">{{ list.name }}</SelectItem>
            </SelectContent>
          </Select>
          <Input
            v-else
            :id="`template-${parameter.name}`"
            v-model="values[parameter.name]"
            v-bind="aria"
            :type="parameter.type === 'Folder' ? 'text' : 'number'"
            :class="{ 'font-mono': parameter.type === 'Folder' }"
          />
        </FormField>
        <p v-if="errors._" class="text-destructive text-sm">{{ errors._ }}</p>
        <DialogFooter class="sm:justify-between">
          <Button type="button" variant="ghost" @click="selected = null">
            <ArrowLeftIcon />
            {{ $t('common.back') }}
          </Button>
          <Button type="submit" :disabled="busy" data-testid="use-template">
            <Loader2Icon v-if="busy" class="animate-spin" />
            {{ $t('rules.templates.use') }}
          </Button>
        </DialogFooter>
      </form>
    </DialogContent>
  </Dialog>
</template>
