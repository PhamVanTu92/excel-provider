import { useState, useEffect, useCallback } from 'react'
import { useQuery } from '@tanstack/react-query'
import { checkHealth } from './api/client'
import { getSales } from './api/sales'
import { getProducts } from './api/products'
import SummaryBar from './components/SummaryBar'
import Toast, { type ToastMessage } from './components/Toast'
import SalesTab from './pages/SalesTab'
import ProductsTab from './pages/ProductsTab'

type TabId = 'sales' | 'products'

let _toastId = 0

export default function App() {
  const [activeTab, setActiveTab] = useState<TabId>('sales')
  const [healthy, setHealthy]     = useState<boolean | null>(null)
  const [toasts, setToasts]       = useState<ToastMessage[]>([])

  const today = new Date().toISOString().slice(0, 10)

  // Health check polling every 30s
  useEffect(() => {
    let mounted = true
    async function poll() {
      const ok = await checkHealth()
      if (mounted) setHealthy(ok)
    }
    poll()
    const id = setInterval(poll, 30_000)
    return () => { mounted = false; clearInterval(id) }
  }, [])

  // Prefetch summary data
  const { data: allSales = [] } = useQuery({
    queryKey: ['sales', '', ''],
    queryFn: () => getSales({}),
    staleTime: 60_000,
  })

  const { data: products = [] } = useQuery({
    queryKey: ['products'],
    queryFn: getProducts,
    staleTime: 60_000,
  })

  const addToast = useCallback((type: ToastMessage['type'], text: string) => {
    const id = ++_toastId
    setToasts(t => [...t, { id, type, text }])
  }, [])

  const dismissToast = useCallback((id: number) => {
    setToasts(t => t.filter(m => m.id !== id))
  }, [])

  const TABS: { id: TabId; label: string }[] = [
    { id: 'sales',    label: 'Dữ liệu bán hàng' },
    { id: 'products', label: 'Sản phẩm' },
  ]

  return (
    <div className="min-h-screen bg-slate-50">
      {/* Header */}
      <header className="sticky top-0 z-40 border-b border-slate-200 bg-white shadow-sm">
        <div className="mx-auto max-w-7xl flex items-center gap-4 px-6 py-3">
          <div className="flex items-center gap-2">
            <span className="text-lg font-bold text-slate-800">Excel Provider</span>
            <span className="hidden text-slate-400 sm:inline">—</span>
            <span className="hidden text-sm text-slate-500 sm:inline">Quản lý dữ liệu</span>
          </div>
          <div className="flex-1" />
          {/* Connection indicator */}
          <div className="flex items-center gap-1.5 text-xs">
            <span
              className={`h-2 w-2 rounded-full ${
                healthy === null  ? 'bg-slate-300' :
                healthy           ? 'bg-green-500 animate-pulse' :
                                    'bg-red-500'
              }`}
            />
            <span className={`font-medium ${
              healthy === null  ? 'text-slate-400' :
              healthy           ? 'text-green-600' :
                                  'text-red-600'
            }`}>
              {healthy === null ? 'Đang kiểm tra…' : healthy ? 'API hoạt động' : 'API không phản hồi'}
            </span>
          </div>
        </div>
      </header>

      <main className="mx-auto max-w-7xl px-6 py-6 space-y-6">
        {/* Summary bar */}
        <SummaryBar sales={allSales} products={products} todayDate={today} />

        {/* Tab navigation */}
        <div className="border-b border-slate-200">
          <nav className="flex gap-1">
            {TABS.map(tab => (
              <button
                key={tab.id}
                onClick={() => setActiveTab(tab.id)}
                className={`px-4 py-2.5 text-sm font-medium transition-colors border-b-2 -mb-px ${
                  activeTab === tab.id
                    ? 'border-blue-600 text-blue-600'
                    : 'border-transparent text-slate-500 hover:text-slate-700 hover:border-slate-300'
                }`}
              >
                {tab.label}
              </button>
            ))}
          </nav>
        </div>

        {/* Tab content */}
        {activeTab === 'sales'    && <SalesTab    addToast={addToast} />}
        {activeTab === 'products' && <ProductsTab addToast={addToast} />}
      </main>

      <Toast messages={toasts} onDismiss={dismissToast} />
    </div>
  )
}
