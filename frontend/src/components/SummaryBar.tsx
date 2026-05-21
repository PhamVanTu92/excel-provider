import type { SaleRecord } from '../api/sales'
import type { ProductRecord } from '../api/products'

interface Props {
  sales: SaleRecord[]
  products: ProductRecord[]
  todayDate: string   // yyyy-MM-dd
}

function fmt(n: number) {
  return n.toLocaleString('vi-VN', { style: 'currency', currency: 'VND', maximumFractionDigits: 0 })
}

export default function SummaryBar({ sales, products, todayDate }: Props) {
  const todaySales    = sales.filter(s => s.date === todayDate)
  const todayRevenue  = todaySales.reduce((acc, s) => acc + s.revenue, 0)
  const outOfStock    = products.filter(p => p.status === 'Out of Stock').length
  const lowStock      = products.filter(p => p.status === 'Low Stock').length

  const cards = [
    { label: 'Giao dịch hôm nay',  value: todaySales.length.toString(),   color: 'text-blue-600'  },
    { label: 'Doanh thu hôm nay',  value: fmt(todayRevenue),              color: 'text-green-600' },
    { label: 'Sản phẩm hết hàng',  value: outOfStock.toString(),          color: 'text-red-600'   },
    { label: 'Sản phẩm tồn thấp',  value: lowStock.toString(),            color: 'text-yellow-600'},
  ]

  return (
    <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
      {cards.map(c => (
        <div key={c.label} className="rounded-xl border border-slate-200 bg-white px-5 py-4 shadow-sm">
          <p className="text-xs font-medium text-slate-500">{c.label}</p>
          <p className={`mt-1 text-2xl font-bold ${c.color}`}>{c.value}</p>
        </div>
      ))}
    </div>
  )
}
