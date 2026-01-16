/**
 * Test data generators for order load tests.
 */

/**
 * Generate a UUID v4.
 */
export function uuidv4(): string {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    const v = c === 'x' ? r : (r & 0x3) | 0x8;
    return v.toString(16);
  });
}

/**
 * Generate a random integer between min and max (inclusive).
 */
export function randomIntBetween(min: number, max: number): number {
  return Math.floor(Math.random() * (max - min + 1)) + min;
}

/**
 * Generate a random string of specified length.
 */
export function randomString(length: number): string {
  const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  let result = '';
  for (let i = 0; i < length; i++) {
    result += chars.charAt(Math.floor(Math.random() * chars.length));
  }
  return result;
}

/**
 * Product catalog for realistic test data.
 */
const PRODUCTS = [
  { name: 'Laptop Pro 15"', minPrice: 999, maxPrice: 1999 },
  { name: 'Wireless Mouse', minPrice: 29, maxPrice: 79 },
  { name: 'Mechanical Keyboard', minPrice: 89, maxPrice: 199 },
  { name: 'USB-C Hub', minPrice: 39, maxPrice: 99 },
  { name: 'Monitor 27" 4K', minPrice: 399, maxPrice: 799 },
  { name: 'Webcam HD', minPrice: 49, maxPrice: 149 },
  { name: 'Headphones', minPrice: 79, maxPrice: 299 },
  { name: 'External SSD 1TB', minPrice: 89, maxPrice: 179 },
  { name: 'Laptop Stand', minPrice: 29, maxPrice: 89 },
  { name: 'Cable Management Kit', minPrice: 19, maxPrice: 49 },
];

const CARRIERS = ['FedEx', 'UPS', 'DHL', 'USPS'];

const CANCELLATION_REASONS = [
  'Changed my mind',
  'Found a better price elsewhere',
  'Ordered by mistake',
  'No longer needed',
  'Delivery time too long',
];

const ORDER_NOTES = [
  'Please leave at front door',
  'Gift wrapping requested',
  'Fragile - handle with care',
  'Call before delivery',
  'Business address - deliver Mon-Fri only',
];

export interface LineItem {
  productId: string;
  productName: string;
  quantity: number;
  unitPrice: number;
}

/**
 * Generate a random line item for an order.
 */
export function generateLineItem(): LineItem {
  const product = PRODUCTS[randomIntBetween(0, PRODUCTS.length - 1)];
  return {
    productId: uuidv4(),
    productName: product.name,
    quantity: randomIntBetween(1, 5),
    unitPrice: randomIntBetween(product.minPrice, product.maxPrice),
  };
}

/**
 * Generate multiple line items.
 */
export function generateLineItems(count?: number): LineItem[] {
  const itemCount = count ?? randomIntBetween(1, 4);
  const items: LineItem[] = [];
  for (let i = 0; i < itemCount; i++) {
    items.push(generateLineItem());
  }
  return items;
}

/**
 * Generate a customer ID.
 */
export function generateCustomerId(): string {
  return uuidv4();
}

/**
 * Generate order notes (30% chance of having notes).
 */
export function generateNotes(): string | undefined {
  if (Math.random() < 0.3) {
    return undefined;
  }
  return ORDER_NOTES[randomIntBetween(0, ORDER_NOTES.length - 1)];
}

/**
 * Generate shipping information.
 */
export function generateShippingInfo(): { trackingNumber: string; carrier: string } {
  return {
    trackingNumber: `${randomString(2).toUpperCase()}${randomIntBetween(100000000, 999999999)}`,
    carrier: CARRIERS[randomIntBetween(0, CARRIERS.length - 1)],
  };
}

/**
 * Generate a cancellation reason.
 */
export function generateCancellationReason(): string {
  return CANCELLATION_REASONS[randomIntBetween(0, CANCELLATION_REASONS.length - 1)];
}

/**
 * Generate a complete create order input.
 */
export function generateCreateOrderInput(): {
  customerId: string;
  lineItems: LineItem[];
  notes?: string;
} {
  return {
    customerId: generateCustomerId(),
    lineItems: generateLineItems(),
    notes: generateNotes(),
  };
}

// Payment-related generators

const PAYMENT_METHODS = ['credit_card', 'debit_card', 'paypal', 'bank_transfer', 'apple_pay'];
const CURRENCIES = ['USD', 'EUR', 'GBP'];
const REFUND_REASONS = [
  'Customer requested refund',
  'Item not as described',
  'Duplicate charge',
  'Order cancelled',
  'Quality issues',
];

/**
 * Generate payment method.
 */
export function generatePaymentMethod(): string {
  return PAYMENT_METHODS[randomIntBetween(0, PAYMENT_METHODS.length - 1)];
}

/**
 * Generate currency.
 */
export function generateCurrency(): string {
  return CURRENCIES[randomIntBetween(0, CURRENCIES.length - 1)];
}

/**
 * Generate authorization code (simulating payment provider response).
 */
export function generateAuthorizationCode(): string {
  return `AUTH-${randomString(8).toUpperCase()}`;
}

/**
 * Generate refund reason.
 */
export function generateRefundReason(): string {
  return REFUND_REASONS[randomIntBetween(0, REFUND_REASONS.length - 1)];
}

/**
 * Generate payment info for initiating a payment.
 */
export function generatePaymentInfo(orderTotal: number): {
  amount: number;
  currency: string;
  paymentMethod: string;
} {
  return {
    amount: orderTotal,
    currency: generateCurrency(),
    paymentMethod: generatePaymentMethod(),
  };
}
