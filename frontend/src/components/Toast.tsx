import { useEffect } from 'react'

export type ToastType = 'success' | 'error' | 'info'

export interface ToastMessage {
  id: number
  type: ToastType
  text: string
}

interface Props {
  messages: ToastMessage[]
  onDismiss: (id: number) => void
}

const ICONS: Record<ToastType, string> = {
  success: '✓',
  error:   '✕',
  info:    'i',
}

const COLORS: Record<ToastType, string> = {
  success: 'bg-green-600',
  error:   'bg-red-600',
  info:    'bg-blue-600',
}

function ToastItem({ msg, onDismiss }: { msg: ToastMessage; onDismiss: (id: number) => void }) {
  useEffect(() => {
    const t = setTimeout(() => onDismiss(msg.id), 4000)
    return () => clearTimeout(t)
  }, [msg.id, onDismiss])

  return (
    <div className="flex items-start gap-3 rounded-lg bg-white shadow-lg border border-slate-200 p-4 min-w-[280px] max-w-sm">
      <span className={`mt-0.5 flex h-5 w-5 flex-shrink-0 items-center justify-center rounded-full text-xs font-bold text-white ${COLORS[msg.type]}`}>
        {ICONS[msg.type]}
      </span>
      <p className="flex-1 text-sm text-slate-700">{msg.text}</p>
      <button
        onClick={() => onDismiss(msg.id)}
        className="text-slate-400 hover:text-slate-600 text-lg leading-none"
      >
        ×
      </button>
    </div>
  )
}

export default function Toast({ messages, onDismiss }: Props) {
  if (messages.length === 0) return null
  return (
    <div className="fixed bottom-5 right-5 z-50 flex flex-col gap-2">
      {messages.map(m => (
        <ToastItem key={m.id} msg={m} onDismiss={onDismiss} />
      ))}
    </div>
  )
}
