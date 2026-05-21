import { useState, useRef, useCallback } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { getProducts, updateProductStock, type ProductRecord } from '../api/products'
import StatusBadge from '../components/StatusBadge'
import type { ToastMessage } from '../components/Toast'

interface Props {
  addToast: (type: ToastMessage['type'], text: string) => void
}

interface InlineEdit {
  productId: string
  value: string
}

export default function ProductsTab({ addToast }: Props) {
  const qc = useQueryClient()
  const [inlineEdit, setInlineEdit] = useState<InlineEdit | null>(null)
  const inputRef = useRef<HTMLInputElement>(null)

  const { data: products = [], isLoading, isError } = useQuery({
    queryKey: ['products'],
    queryFn: getProducts,
  })

  const stockMut = useMutation({
    mutationFn: ({ productId, stock }: { productId: string; stock: number }) =>
      updateProductStock(productId, stock),
    onSuccess: (_data, vars) => {
      qc.invalidateQueries({ queryKey: ['products'] })
      addToast('success', `Đã cập nhật tồn kho — ${vars.stock} đơn vị`)
    },
    onError: (err: Error) => addToast('error', `Lỗi: ${err.message}`),
  })

  const startEdit = useCallback((p: ProductRecord) => {
    setInlineEdit({ productId: p.productId, value: String(p.currentStock) })
    setTimeout(() => inputRef.current?.focus(), 50)
  }, [])

  const commitEdit = useCallback(async (productId: string) => {
    if (!inlineEdit || inlineEdit.productId !== productId) return
    const parsed = parseInt(inlineEdit.value, 10)
    if (isNaN(parsed) || parsed < 0) {
      addToast('error', 'Số lượng tồn kho không hợp lệ')
      setInlineEdit(null)
      return
    }
    setInlineEdit(null)
    await stockMut.mutateAsync({ productId, stock: parsed })
  }, [inlineEdit, stockMut, addToast])

  const handleKeyDown = useCallback((e: React.KeyboardEvent, productId: string) => {
    if (e.key === 'Enter')  commitEdit(productId)
    if (e.key === 'Escape') setInlineEdit(null)
  }, [commitEdit])

  return (
    <div className="rounded-xl border border-slate-200 bg-white shadow-sm overflow-hidden">
      {isLoading && (
        <div className="py-12 text-center text-sm text-slate-500">Đang tải…</div>
      )}
      {isError && (
        <div className="py-12 text-center text-sm text-red-500">Không thể tải dữ liệu. Kiểm tra kết nối API.</div>
      )}
      {!isLoading && !isError && (
        <div className="overflow-x-auto">
          <table className="min-w-full divide-y divide-slate-200">
            <thead className="bg-slate-50">
              <tr>
                {['Tên sản phẩm', 'Danh mục', 'Giá', 'Tồn kho', 'Tồn tối thiểu', 'Trạng thái'].map(h => (
                  <th key={h} className="table-th">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {products.length === 0 && (
                <tr>
                  <td colSpan={6} className="py-10 text-center text-sm text-slate-400">
                    Không có dữ liệu sản phẩm
                  </td>
                </tr>
              )}
              {products.map(p => {
                const isEditing = inlineEdit?.productId === p.productId
                return (
                  <tr key={p.productId} className="hover:bg-slate-50 transition-colors">
                    <td className="table-td font-medium">{p.name}</td>
                    <td className="table-td text-slate-500">{p.category}</td>
                    <td className="table-td text-right font-semibold">
                      {p.price.toLocaleString('vi-VN')}₫
                    </td>
                    <td className="table-td">
                      {isEditing ? (
                        <input
                          ref={inputRef}
                          type="number"
                          min={0}
                          className="input w-24 text-right"
                          value={inlineEdit.value}
                          onChange={e => setInlineEdit({ ...inlineEdit, value: e.target.value })}
                          onBlur={() => commitEdit(p.productId)}
                          onKeyDown={e => handleKeyDown(e, p.productId)}
                        />
                      ) : (
                        <button
                          className="group flex items-center gap-1 rounded px-2 py-0.5 text-sm font-semibold text-slate-700 hover:bg-blue-50 hover:text-blue-700 transition-colors"
                          onClick={() => startEdit(p)}
                          title="Bấm để chỉnh sửa tồn kho"
                        >
                          {p.currentStock}
                          <span className="invisible text-xs text-blue-400 group-hover:visible">✎</span>
                        </button>
                      )}
                    </td>
                    <td className="table-td text-right text-slate-500">{p.minStock}</td>
                    <td className="table-td">
                      <StatusBadge status={p.status as ProductRecord['status']} />
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
