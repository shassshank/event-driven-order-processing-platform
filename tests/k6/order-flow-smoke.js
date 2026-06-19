import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
  vus: 5,
  duration: '30s',
  thresholds: {
    http_req_failed: ['rate<0.05'],
    http_req_duration: ['p(95)<1500'],
  },
};

const gatewayUrl = __ENV.GATEWAY_URL || 'http://localhost:8090';
const apiKey = __ENV.GATEWAY_API_KEY || 'local-dev-key';

export default function () {
  const suffix = `${__VU}-${__ITER}-${Date.now()}`;
  const payload = JSON.stringify({
    customerId: '22222222-2222-2222-2222-222222222222',
    clientRequestId: `k6-${suffix}`,
    currency: 'USD',
    items: [
      {
        productId: '33333333-3333-3333-3333-333333333333',
        quantity: 1,
        unitPrice: 12.34,
      },
    ],
  });

  const response = http.post(`${gatewayUrl}/api/orders`, payload, {
    headers: {
      'Content-Type': 'application/json',
      'X-Demo-Api-Key': apiKey,
      'X-Correlation-ID': `k6-${suffix}`,
    },
  });

  check(response, {
    'create order returned 201 or conflict after stock exhaustion': (r) => r.status === 201 || r.status === 409,
  });

  sleep(1);
}
