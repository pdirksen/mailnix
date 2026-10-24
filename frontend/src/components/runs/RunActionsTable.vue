<script setup lang="ts">
import { Badge } from '@/components/ui/badge'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { formatDateTime } from '@/lib/format'
import { describeAction } from '@/rules/describe'
import type { RunDetail } from '@/stores/runs'
import { useI18n } from 'vue-i18n'

defineProps<{ actions: RunDetail['actions'] }>()

const { t, locale } = useI18n()
</script>

<template>
  <p v-if="actions.length === 0" class="text-muted-foreground text-sm">{{ $t('activity.noActions') }}</p>
  <div v-else class="overflow-x-auto">
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead class="w-36">{{ $t('activity.time') }}</TableHead>
          <TableHead>{{ $t('activity.message') }}</TableHead>
          <TableHead>{{ $t('activity.rule') }}</TableHead>
          <TableHead>{{ $t('activity.action') }}</TableHead>
          <TableHead>{{ $t('activity.result') }}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        <TableRow v-for="action in actions" :key="action.id" :data-testid="`action-${action.action}`">
          <TableCell class="text-muted-foreground text-xs whitespace-nowrap">{{ formatDateTime(action.createdAt, locale) }}</TableCell>
          <TableCell class="max-w-72">
            <p class="truncate text-sm font-medium">{{ action.subject || '—' }}</p>
            <p class="text-muted-foreground truncate text-xs">{{ action.from }}</p>
          </TableCell>
          <TableCell class="text-sm">{{ action.ruleName ?? '—' }}</TableCell>
          <TableCell class="text-sm">
            {{ describeAction({ type: action.action, targetFolder: action.targetFolder, keyword: action.keyword }, t) }}
          </TableCell>
          <TableCell>
            <div class="flex flex-wrap gap-1">
              <Badge v-if="action.dryRun" variant="outline">{{ $t('activity.dryRun') }}</Badge>
              <Badge v-if="!action.success" variant="outline" class="border-destructive/50 text-destructive" :title="action.error ?? undefined">
                {{ $t('activity.failed') }}
              </Badge>
              <Badge v-if="action.undoneAt" variant="outline" class="text-muted-foreground">{{ $t('activity.undone') }}</Badge>
            </div>
            <p v-if="action.error" class="text-destructive mt-1 max-w-64 text-xs break-words">{{ action.error }}</p>
          </TableCell>
        </TableRow>
      </TableBody>
    </Table>
  </div>
</template>
