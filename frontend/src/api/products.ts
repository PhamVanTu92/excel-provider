import { apiFetch } from './client'

export type ProductStatus = 'In Stock' | 'Low Stock' | 'Out of Stock'

export interface ProductRecord {
  productId: string
  name: string
  category: string
  price: number
  currentStock: number
  minStock: number
  status: ProductStatus
}

export function getProducts(): Promise<ProductRecord[]> {
  return apiFetch<ProductRecord[]>('/api/products')
}

export function updateProductStock(productId: string, stock: number): Promise<void> {
  return apiFetch<void>(`/api/products/${productId}/stock`, {
    method: 'PUT',
    body: JSON.stringify({ stock }),
  })
}
